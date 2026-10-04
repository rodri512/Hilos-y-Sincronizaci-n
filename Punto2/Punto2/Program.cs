using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

class Program
{
    const int LIMITE = 1_000_000;
    const int CORRIDAS = 5;          // corridas pedidas en el enunciado
    const int CORRIDAS_EXTRA = 100;  // corridas adicionales SOLO para estimar la probabilidad de carrera
    const int ESPERADO = 78498;      // cantidad real de primos <= 1.000.000

    // Variable global compartida (SIN sincronización a propósito, para poder observar condiciones de carrera)
    static int contadorGlobal;

    static bool EsPrimo(int n)
    {
        if (n < 2) return false;
        if (n < 4) return true;
        if (n % 2 == 0) return false;
        for (int i = 3; (long)i * i <= n; i += 2)
            if (n % i == 0) return false;
        return true;
    }

    // ---------- Versión 1: secuencial ----------
    static int Secuencial()
    {
        int contador = 0;
        for (int n = 1; n <= LIMITE; n++)
            if (EsPrimo(n)) contador++;
        return contador;
    }

    // ---------- Versión 2: con hilos y variable global compartida ----------
    static void ContarIntervalo(int inicio, int fin)
    {
        for (int n = inicio; n <= fin; n++)
        {
            if (EsPrimo(n))
                contadorGlobal++;   // <-- NO es atómico: lectura, suma, escritura (posible condición de carrera)
        }
    }

    static int ConHilos(int numHilos)
    {
        contadorGlobal = 0;
        var hilos = new Thread[numHilos];
        int tamano = LIMITE / numHilos;

        for (int i = 0; i < numHilos; i++)
        {
            int inicio = i * tamano + 1;
            int fin = (i == numHilos - 1) ? LIMITE : (i + 1) * tamano;
            hilos[i] = new Thread(() => ContarIntervalo(inicio, fin));
        }

        foreach (var h in hilos) h.Start();
        foreach (var h in hilos) h.Join();

        return contadorGlobal;
    }

    // ---------- Utilidades de medición ----------
    class Resultado
    {
        public string Nombre = "";
        public List<double> Tiempos = new();
        public List<int> Conteos = new();
        public double Promedio => Tiempos.Average();
        public int CorridasConError => Conteos.Count(c => c != ESPERADO);
    }

    static Resultado Medir(string nombre, Func<int> version, int corridas)
    {
        var r = new Resultado { Nombre = nombre };
        for (int i = 1; i <= corridas; i++)
        {
            var sw = Stopwatch.StartNew();
            int primos = version();
            sw.Stop();

            r.Tiempos.Add(sw.Elapsed.TotalMilliseconds);
            r.Conteos.Add(primos);

            string marca = primos == ESPERADO ? "" : "  <-- ¡CONDICIÓN DE CARRERA!";
            Console.WriteLine($"  Corrida {i}: {sw.Elapsed.TotalMilliseconds,9:F2} ms | primos = {primos}{marca}");
        }
        Console.WriteLine($"  >> Tiempo promedio: {r.Promedio:F2} ms\n");
        return r;
    }

    static void Main()
    {
        Console.WriteLine($"Núcleos lógicos disponibles: {Environment.ProcessorCount}");
        Console.WriteLine($"Primos esperados entre 1 y {LIMITE:N0}: {ESPERADO}\n");

        // Calentamiento (JIT) para que la primera corrida no salga penalizada
        Secuencial();
        ConHilos(2);
        ConHilos(4);

        Console.WriteLine("=== VERSIÓN SECUENCIAL ===");
        var seq = Medir("Secuencial", Secuencial, CORRIDAS);

        Console.WriteLine("=== VERSIÓN CON 2 HILOS ===");
        var h2 = Medir("2 hilos", () => ConHilos(2), CORRIDAS);

        Console.WriteLine("=== VERSIÓN CON 4 HILOS ===");
        var h4 = Medir("4 hilos", () => ConHilos(4), CORRIDAS);

        // ---------- Comparación de tiempos ----------
        Console.WriteLine("=== COMPARACIÓN DE TIEMPOS (5 corridas) ===");
        Console.WriteLine($"{"Versión",-12}{"Promedio (ms)",15}{"Speedup",10}{"Primos distintos de " + ESPERADO,28}");
        foreach (var r in new[] { seq, h2, h4 })
            Console.WriteLine($"{r.Nombre,-12}{r.Promedio,15:F2}{seq.Promedio / r.Promedio,9:F2}x{r.CorridasConError,18} de {CORRIDAS}");

        // ---------- Estimación de la probabilidad de condición de carrera ----------
        Console.WriteLine($"\n=== PROBABILIDAD DE CONDICIÓN DE CARRERA ({CORRIDAS_EXTRA} corridas extra por versión) ===");
        Console.WriteLine("(salida resumida; solo se cuentan corridas con resultado incorrecto)\n");

        int err2 = 0, err4 = 0;
        for (int i = 0; i < CORRIDAS_EXTRA; i++)
        {
            if (ConHilos(2) != ESPERADO) err2++;
            if (ConHilos(4) != ESPERADO) err4++;
        }

        // Se suman también las 5 corridas iniciales
        int total = CORRIDAS + CORRIDAS_EXTRA;
        err2 += h2.CorridasConError;
        err4 += h4.CorridasConError;

        Console.WriteLine($"2 hilos: {err2}/{total} corridas incorrectas  -> {100.0 * err2 / total:F1}%");
        Console.WriteLine($"4 hilos: {err4}/{total} corridas incorrectas  -> {100.0 * err4 / total:F1}%");

        Console.WriteLine("\nPresioná Enter para salir...");
        Console.ReadLine();
    }
    
}