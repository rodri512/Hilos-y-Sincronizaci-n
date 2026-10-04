using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

class Program
{
    const int LIMITE = 1_000_000;
    const int CORRIDAS = 5;
    const int CORRIDAS_EXTRA = 100;   // solo para estimar la probabilidad de error (no se mide tiempo)
    const int ESPERADO = 78498;

    enum Modo
    {
        SinSincronizar,   // punto anterior: contador++ sin protección
        Lock,             // Monitor (espera bloqueante)  <-- versión elegida
        SpinLock,         // espera activa (comparación)
        Interlocked       // operación atómica sin bloqueo (comparación)
    }

    // ----- Estado compartido -----
    static int contadorGlobal;
    static readonly object candado = new object();
    static SpinLock spinLock = new SpinLock(false);   // struct: NO debe ser readonly ni copiarse

    static bool EsPrimo(int n)
    {
        if (n < 2) return false;
        if (n < 4) return true;
        if (n % 2 == 0) return false;
        for (int i = 3; (long)i * i <= n; i += 2)
            if (n % i == 0) return false;
        return true;
    }

    // Sección crítica: el incremento de la variable global compartida
    static void Incrementar(Modo modo)
    {
        switch (modo)
        {
            case Modo.SinSincronizar:
                contadorGlobal++;                       // lectura-suma-escritura: NO atómico
                break;

            case Modo.Lock:
                lock (candado)                          // Monitor.Enter/Exit con try/finally implícito
                {
                    contadorGlobal++;
                }
                break;

            case Modo.SpinLock:
                bool tomado = false;
                try
                {
                    spinLock.Enter(ref tomado);         // espera activa
                    contadorGlobal++;
                }
                finally
                {
                    if (tomado) spinLock.Exit();
                }
                break;

            case Modo.Interlocked:
                Interlocked.Increment(ref contadorGlobal);
                break;
        }
    }

    static void ContarIntervalo(int inicio, int fin, Modo modo)
    {
        for (int n = inicio; n <= fin; n++)
            if (EsPrimo(n))
                Incrementar(modo);
    }

    static int ConHilos(int numHilos, Modo modo)
    {
        contadorGlobal = 0;
        var hilos = new Thread[numHilos];
        int tamano = LIMITE / numHilos;

        for (int i = 0; i < numHilos; i++)
        {
            int inicio = i * tamano + 1;
            int fin = (i == numHilos - 1) ? LIMITE : (i + 1) * tamano;
            hilos[i] = new Thread(() => ContarIntervalo(inicio, fin, modo));
        }

        foreach (var h in hilos) h.Start();
        foreach (var h in hilos) h.Join();

        return contadorGlobal;
    }

    // ----- Medición -----
    class Resultado
    {
        public string Nombre = "";
        public List<double> Tiempos = new();
        public List<int> Conteos = new();
        public double Promedio => Tiempos.Average();
        public int Errores => Conteos.Count(c => c != ESPERADO);
    }

    static Resultado Medir(string nombre, int hilos, Modo modo)
    {
        var r = new Resultado { Nombre = nombre };
        Console.WriteLine($"=== {nombre} ===");
        for (int i = 1; i <= CORRIDAS; i++)
        {
            var sw = Stopwatch.StartNew();
            int primos = ConHilos(hilos, modo);
            sw.Stop();

            r.Tiempos.Add(sw.Elapsed.TotalMilliseconds);
            r.Conteos.Add(primos);
            string marca = primos == ESPERADO ? "" : "  <-- ¡CONDICIÓN DE CARRERA!";
            Console.WriteLine($"  Corrida {i}: {sw.Elapsed.TotalMilliseconds,9:F2} ms | primos = {primos}{marca}");
        }
        Console.WriteLine($"  >> Tiempo promedio: {r.Promedio:F2} ms\n");
        return r;
    }

    static int ContarErrores(int hilos, Modo modo)
    {
        int errores = 0;
        for (int i = 0; i < CORRIDAS_EXTRA; i++)
            if (ConHilos(hilos, modo) != ESPERADO) errores++;
        return errores;
    }

    static void Main()
    {
        Console.WriteLine($"Núcleos lógicos: {Environment.ProcessorCount}");
        Console.WriteLine($"Primos esperados entre 1 y {LIMITE:N0}: {ESPERADO}\n");

        // Calentamiento JIT
        foreach (Modo m in Enum.GetValues(typeof(Modo))) { ConHilos(2, m); ConHilos(4, m); }

        var resultados = new List<Resultado>
        {
            Medir("2 hilos SIN exclusión mutua", 2, Modo.SinSincronizar),
            Medir("2 hilos CON lock (Monitor)",  2, Modo.Lock),
            Medir("4 hilos SIN exclusión mutua", 4, Modo.SinSincronizar),
            Medir("4 hilos CON lock (Monitor)",  4, Modo.Lock),
            // Comparación adicional con otras primitivas
            Medir("4 hilos CON SpinLock",        4, Modo.SpinLock),
            Medir("4 hilos CON Interlocked",     4, Modo.Interlocked),
        };

        Console.WriteLine("=== RESUMEN (5 corridas) ===");
        Console.WriteLine($"{"Versión",-30}{"Promedio (ms)",15}{"Primos (última)",18}{"Corridas erróneas",20}");
        foreach (var r in resultados)
            Console.WriteLine($"{r.Nombre,-30}{r.Promedio,15:F2}{r.Conteos.Last(),18}{r.Errores,15} de {CORRIDAS}");

        // Probabilidad de error: sin protección vs con lock
        Console.WriteLine($"\n=== PROBABILIDAD DE ERROR ({CORRIDAS_EXTRA} corridas extra) ===");
        foreach (int h in new[] { 2, 4 })
        {
            int eSin = ContarErrores(h, Modo.SinSincronizar);
            int eLock = ContarErrores(h, Modo.Lock);
            Console.WriteLine($"{h} hilos | sin exclusión: {eSin}/{CORRIDAS_EXTRA} ({100.0 * eSin / CORRIDAS_EXTRA:F1}%)" +
                              $" | con lock: {eLock}/{CORRIDAS_EXTRA} ({100.0 * eLock / CORRIDAS_EXTRA:F1}%)");
        }

        Console.WriteLine("\nPresioná Enter para salir...");
        Console.ReadLine();
    }
}
