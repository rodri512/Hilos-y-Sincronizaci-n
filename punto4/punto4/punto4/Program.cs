using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ColaImpresion
{
    // =====================================================================
    //  MODELO
    // =====================================================================
    enum Estado { Activo, Bloqueado, Terminado }

    class Trabajo
    {
        public int App;          // 0..2
        public int Num;          // 1..100
        public double TEncolado; // instante (simulado, en s) en que entró a la cola
    }

    class Snapshot
    {
        public double Tiempo;
        public string[] Cola;
        public int[] Enviados, ImpresosDeApp, Bloqueos;
        public Estado[] EstadoApp;
        public int Impresos, JobActual;
        public Estado EstadoImp;
        public bool Terminado;
    }

    // =====================================================================
    //  SIMULADOR (hilos Aplicación 1..3 + hilo Impresora)
    // =====================================================================
    class Simulador
    {
        public const int NUM_APPS = 3;

        readonly int capacidad, trabajosPorApp;
        readonly double escala;       // factor de aceleración del tiempo (1 = tiempo real)
        readonly bool impresoraLenta; // para forzar que la cola se llene y haya bloqueos

        // ---- Recurso compartido: la cola de impresión ----
        readonly Queue<Trabajo> cola = new Queue<Trabajo>();   // Queue<T> NO es thread-safe
        readonly object candado = new object();                // protege 'cola' y las estadísticas
        readonly SemaphoreSlim espacios;                       // cuenta lugares libres (empieza en capacidad)
        readonly SemaphoreSlim items;                          // cuenta trabajos disponibles (empieza en 0)

        readonly Stopwatch reloj = new Stopwatch();
        public readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();

        // ---- Estado y estadísticas ----
        readonly Estado[] estadoApp = new Estado[NUM_APPS];
        readonly int[] enviados = new int[NUM_APPS];
        readonly int[] impresosDeApp = new int[NUM_APPS];
        readonly int[] bloqueos = new int[NUM_APPS];
        readonly double[] tiempoBloqueada = new double[NUM_APPS];
        readonly double[] ultimoEnvio = new double[NUM_APPS];

        Estado estadoImp = Estado.Activo;
        int impresos, jobActual, bloqueosImp;
        double tiempoOcupada, tiempoBloqImp;
        double areaCola, ultCambio;         // integral (longitud x tiempo) para el promedio ponderado
        double sumaEspera, maxEspera, tFin;
        volatile bool terminado;

        public Simulador(int capacidad, int trabajosPorApp, double escala, bool impresoraLenta)
        {
            this.capacidad = capacidad;
            this.trabajosPorApp = trabajosPorApp;
            this.escala = escala;
            this.impresoraLenta = impresoraLenta;
            espacios = new SemaphoreSlim(capacidad, capacidad);
            items = new SemaphoreSlim(0);
        }

        double Ahora() { return reloj.Elapsed.TotalSeconds * escala; }   // tiempo SIMULADO en segundos
        void Dormir(double segSimulados) { Thread.Sleep(Math.Max(1, (int)(segSimulados * 1000 / escala))); }
        static Random NuevoRandom() { return new Random(Guid.NewGuid().GetHashCode()); }
        void Escribir(string msg) { Log.Enqueue("[" + Ahora().ToString("F1") + " s] " + msg); }

        // Debe llamarse SIEMPRE dentro del lock y ANTES de modificar la cola
        void AcumularArea(double ahora)
        {
            areaCola += cola.Count * (ahora - ultCambio);
            ultCambio = ahora;
        }

        public void Iniciar()
        {
            reloj.Start();
            for (int i = 0; i < NUM_APPS; i++)
            {
                int x = i;
                new Thread(() => Aplicacion(x)) { IsBackground = true, Name = "Aplicación " + (x + 1) }.Start();
            }
            new Thread(Impresora) { IsBackground = true, Name = "Impresora" }.Start();
        }

        // ------------------------- PRODUCTOR -------------------------
        void Aplicacion(int x)
        {
            var rnd = NuevoRandom();
            for (int n = 1; n <= trabajosPorApp; n++)
            {
                estadoApp[x] = Estado.Activo;
                Dormir(0.5 + rnd.NextDouble() * 4.0);        // genera el documento: 0,5 a 4,5 s

                // ¿Hay lugar? Si no, la aplicación se BLOQUEA hasta que la impresora libere uno
                if (!espacios.Wait(0))
                {
                    double ini = Ahora();
                    estadoApp[x] = Estado.Bloqueado;
                    bloqueos[x]++;
                    espacios.Wait();                          // espera bloqueante
                    tiempoBloqueada[x] += Ahora() - ini;
                    estadoApp[x] = Estado.Activo;
                }

                int pos;
                // ===== REGIÓN CRÍTICA 1: encolar =====
                lock (candado)
                {
                    double t = Ahora();
                    AcumularArea(t);
                    cola.Enqueue(new Trabajo { App = x, Num = n, TEncolado = t });
                    pos = cola.Count;                          // posición real, medida dentro del lock
                    enviados[x]++;
                    ultimoEnvio[x] = t;
                }
                // ======================================
                items.Release();                               // avisa a la impresora: hay un trabajo más

                Escribir("Aplicación " + (x + 1) + " imprimió su trabajo " + n +
                         ", quedando en la posición " + pos + " de la cola");
            }
            estadoApp[x] = Estado.Terminado;
        }

        // ------------------------- CONSUMIDOR -------------------------
        void Impresora()
        {
            var rnd = NuevoRandom();
            int total = NUM_APPS * trabajosPorApp;
            for (int i = 1; i <= total; i++)
            {
                // ¿Hay trabajos? Si no, la impresora se BLOQUEA (nunca intenta sacar de una cola vacía)
                if (!items.Wait(0))
                {
                    double ini = Ahora();
                    estadoImp = Estado.Bloqueado;
                    bloqueosImp++;
                    items.Wait();                              // espera bloqueante
                    tiempoBloqImp += Ahora() - ini;
                }

                Trabajo t;
                // ===== REGIÓN CRÍTICA 2: desencolar =====
                lock (candado)
                {
                    double ahora = Ahora();
                    AcumularArea(ahora);
                    t = cola.Dequeue();
                    double espera = ahora - t.TEncolado;
                    sumaEspera += espera;
                    if (espera > maxEspera) maxEspera = espera;
                    jobActual = i;
                    estadoImp = Estado.Activo;
                }
                // ========================================
                espacios.Release();                            // libera un lugar: puede despertar a una aplicación

                Escribir("Impresora imprimiendo trabajo " + i + " (" + i + "-ésimo trabajo de impresión; " +
                         "de Aplicación " + (t.App + 1) + ", trabajo " + t.Num + ")");

                double ini2 = Ahora();
                Dormir(impresoraLenta ? 2.0 + rnd.NextDouble() : 0.5 + rnd.NextDouble() * 0.5);
                lock (candado)
                {
                    tiempoOcupada += Ahora() - ini2;
                    impresos++;
                    impresosDeApp[t.App]++;
                }
            }
            lock (candado)
            {
                tFin = Ahora();
                AcumularArea(tFin);
                estadoImp = Estado.Terminado;
                terminado = true;
            }
        }

        // ------------------------- CONSULTAS PARA LA INTERFAZ -------------------------
        public Snapshot Tomar()
        {
            lock (candado)
            {
                return new Snapshot
                {
                    Tiempo = terminado ? tFin : Ahora(),
                    Cola = cola.Select(t => "A" + (t.App + 1) + "-" + t.Num).ToArray(),
                    Enviados = (int[])enviados.Clone(),
                    ImpresosDeApp = (int[])impresosDeApp.Clone(),
                    Bloqueos = (int[])bloqueos.Clone(),
                    EstadoApp = (Estado[])estadoApp.Clone(),
                    Impresos = impresos,
                    JobActual = jobActual,
                    EstadoImp = estadoImp,
                    Terminado = terminado
                };
            }
        }

        public string Reporte()
        {
            lock (candado)
            {
                Func<double, string> F = v => v.ToString("F2");
                int total = NUM_APPS * trabajosPorApp;
                var sb = new StringBuilder();
                sb.AppendLine("Tiempos en segundos simulados.");
                sb.AppendLine();
                sb.AppendLine("Duración total de la simulación: " + F(tFin) + " s");
                for (int i = 0; i < NUM_APPS; i++)
                    sb.AppendLine("  Aplicación " + (i + 1) + " envió su último trabajo en t = " + F(ultimoEnvio[i]) + " s");
                sb.AppendLine();
                sb.AppendLine("Longitud promedio de la cola: " + F(areaCola / tFin) + " trabajos");
                sb.AppendLine("Tiempo promedio de espera en cola: " + F(sumaEspera / total) + " s");
                sb.AppendLine("Tiempo máximo de espera en cola: " + F(maxEspera) + " s");
                sb.AppendLine("Utilización de la impresora: " + F(100.0 * tiempoOcupada / tFin) + " %");
                sb.AppendLine();
                int totBloq = 0; double totTBloq = 0;
                for (int i = 0; i < NUM_APPS; i++)
                {
                    sb.AppendLine("Aplicación " + (i + 1) + ": " + bloqueos[i] + " bloqueos, " + F(tiempoBloqueada[i]) + " s bloqueada");
                    totBloq += bloqueos[i]; totTBloq += tiempoBloqueada[i];
                }
                sb.AppendLine("TOTAL aplicaciones: " + totBloq + " bloqueos, " + F(totTBloq) + " s bloqueadas");
                sb.AppendLine();
                sb.AppendLine("Impresora: " + bloqueosImp + " bloqueos (cola vacía), " + F(tiempoBloqImp) + " s esperando");
                return sb.ToString();
            }
        }
    }

    // =====================================================================
    //  INTERFAZ GRÁFICA
    // =====================================================================
    class PanelCola : Panel
    {
        public string[] Items = new string[0];
        public int Capacidad = 10;

        public PanelCola() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Capacidad <= 0) return;
            var g = e.Graphics;
            float w = (Width - 4f) / Capacidad;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i < Capacidad; i++)
                {
                    var r = new RectangleF(2 + i * w, 2, w - 4, Height - 4);
                    bool ocupado = Items != null && i < Items.Length;
                    using (var br = new SolidBrush(ocupado ? Color.SteelBlue : Color.WhiteSmoke))
                        g.FillRectangle(br, r);
                    g.DrawRectangle(Pens.Gray, r.X, r.Y, r.Width, r.Height);
                    if (ocupado) g.DrawString(Items[i], Font, Brushes.White, r, sf);
                }
            }
        }
    }

    class FormPrincipal : Form
    {
        NumericUpDown nudVel, nudCap;
        CheckBox chkLenta;
        Button btnIniciar;
        Label lblTiempo, lblImpEstado, lblImpCont, lblColaInfo;
        Label[] lblAppEstado = new Label[3], lblAppCont = new Label[3];
        PanelCola panelCola;
        TextBox txtLog, txtStats;
        Simulador sim;

        static Label Lbl(string texto, int x, int y, int w, bool bold = false)
        {
            return new Label
            {
                Text = texto,
                Left = x,
                Top = y,
                Width = w,
                Height = 22,
                Font = bold ? new Font("Segoe UI", 9.5f, FontStyle.Bold) : new Font("Segoe UI", 9.5f)
            };
        }

        public FormPrincipal()
        {
            Text = "Simulación: cola de impresión (productor-consumidor)";
            ClientSize = new Size(930, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            // ---- Barra de control ----
            Controls.Add(Lbl("Velocidad (x):", 10, 14, 95));
            nudVel = new NumericUpDown { Left = 105, Top = 10, Width = 55, Minimum = 1, Maximum = 20, Value = 10 };
            Controls.Add(nudVel);
            Controls.Add(Lbl("Capacidad cola:", 180, 14, 105));
            nudCap = new NumericUpDown { Left = 285, Top = 10, Width = 55, Minimum = 1, Maximum = 30, Value = 10 };
            Controls.Add(nudCap);
            chkLenta = new CheckBox { Text = "Impresora lenta (2–3 s) para forzar bloqueos", Left = 360, Top = 12, Width = 330 };
            Controls.Add(chkLenta);
            btnIniciar = new Button { Text = "Iniciar", Left = 700, Top = 8, Width = 100, Height = 28 };
            btnIniciar.Click += (s, e) => Iniciar();
            Controls.Add(btnIniciar);
            lblTiempo = Lbl("t = 0.0 s", 815, 14, 110, true);
            Controls.Add(lblTiempo);

            // ---- Aplicaciones ----
            var gbApps = new GroupBox { Text = "Aplicaciones", Left = 10, Top = 45, Width = 910, Height = 115 };
            for (int i = 0; i < 3; i++)
            {
                gbApps.Controls.Add(Lbl("Aplicación " + (i + 1), 10, 25 + i * 28, 100, true));
                lblAppEstado[i] = Lbl("-", 115, 25 + i * 28, 280);
                lblAppCont[i] = Lbl("-", 400, 25 + i * 28, 500);
                gbApps.Controls.Add(lblAppEstado[i]);
                gbApps.Controls.Add(lblAppCont[i]);
            }
            Controls.Add(gbApps);

            // ---- Impresora ----
            var gbImp = new GroupBox { Text = "Impresora", Left = 10, Top = 165, Width = 910, Height = 60 };
            lblImpEstado = Lbl("-", 10, 25, 380, true);
            lblImpCont = Lbl("-", 400, 25, 500);
            gbImp.Controls.Add(lblImpEstado);
            gbImp.Controls.Add(lblImpCont);
            Controls.Add(gbImp);

            // ---- Cola ----
            var gbCola = new GroupBox { Text = "Cola de impresión (izquierda = próximo en imprimirse)", Left = 10, Top = 230, Width = 910, Height = 110 };
            lblColaInfo = Lbl("-", 10, 22, 500, true);
            panelCola = new PanelCola { Left = 10, Top = 48, Width = 890, Height = 52 };
            gbCola.Controls.Add(lblColaInfo);
            gbCola.Controls.Add(panelCola);
            Controls.Add(gbCola);

            // ---- Log y estadísticas ----
            var gbLog = new GroupBox { Text = "Registro de mensajes", Left = 10, Top = 345, Width = 450, Height = 345 };
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
            gbLog.Controls.Add(txtLog);
            Controls.Add(gbLog);

            var gbStats = new GroupBox { Text = "Estadísticas finales", Left = 470, Top = 345, Width = 450, Height = 345 };
            txtStats = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
            gbStats.Controls.Add(txtStats);
            Controls.Add(gbStats);
        }

        void Iniciar()
        {
            btnIniciar.Enabled = nudVel.Enabled = nudCap.Enabled = chkLenta.Enabled = false;
            txtLog.Clear();
            txtStats.Clear();

            int cap = (int)nudCap.Value;
            panelCola.Capacidad = cap;
            sim = new Simulador(cap, 100, (double)nudVel.Value, chkLenta.Checked);
            sim.Iniciar();

            // Hilo dedicado a mantener la interfaz
            new Thread(BucleInterfaz) { IsBackground = true, Name = "Interfaz" }.Start();
        }

        // ---- HILO "INTERFAZ": toma fotos del estado y le pide al hilo de la UI que las dibuje ----
        void BucleInterfaz()
        {
            try
            {
                while (true)
                {
                    Snapshot s = sim.Tomar();
                    var lineas = new List<string>();
                    string l;
                    while (sim.Log.TryDequeue(out l)) lineas.Add(l);

                    BeginInvoke((Action)(() => Refrescar(s, lineas)));

                    if (s.Terminado)
                    {
                        string rep = sim.Reporte();
                        BeginInvoke((Action)(() => Finalizar(rep)));
                        break;
                    }
                    Thread.Sleep(100);
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }   // el formulario se cerró durante la simulación
        }

        // ---- Se ejecuta en el hilo de la UI (único autorizado a tocar los controles) ----
        void Refrescar(Snapshot s, List<string> lineas)
        {
            lblTiempo.Text = "t = " + s.Tiempo.ToString("F1") + " s";

            for (int i = 0; i < 3; i++)
            {
                switch (s.EstadoApp[i])
                {
                    case Estado.Activo: lblAppEstado[i].Text = "Generando documento"; lblAppEstado[i].ForeColor = Color.ForestGreen; break;
                    case Estado.Bloqueado: lblAppEstado[i].Text = "BLOQUEADA (cola llena)"; lblAppEstado[i].ForeColor = Color.Firebrick; break;
                    default: lblAppEstado[i].Text = "Terminó"; lblAppEstado[i].ForeColor = Color.Gray; break;
                }
                lblAppCont[i].Text = "Enviados: " + s.Enviados[i] + "/100   |   Impresos: " + s.ImpresosDeApp[i] +
                                     "/100   |   Bloqueos: " + s.Bloqueos[i];
            }

            switch (s.EstadoImp)
            {
                case Estado.Activo: lblImpEstado.Text = "Imprimiendo trabajo " + s.JobActual; lblImpEstado.ForeColor = Color.ForestGreen; break;
                case Estado.Bloqueado: lblImpEstado.Text = "BLOQUEADA (cola vacía)"; lblImpEstado.ForeColor = Color.Firebrick; break;
                default: lblImpEstado.Text = "Terminó"; lblImpEstado.ForeColor = Color.Gray; break;
            }
            lblImpCont.Text = "Trabajos impresos: " + s.Impresos + "/300";

            lblColaInfo.Text = "Ocupación: " + s.Cola.Length + " / " + panelCola.Capacidad;
            panelCola.Items = s.Cola;
            panelCola.Invalidate();

            if (lineas.Count > 0)
            {
                txtLog.AppendText(string.Join(Environment.NewLine, lineas) + Environment.NewLine);
                if (txtLog.Lines.Length > 400)
                    txtLog.Lines = txtLog.Lines.Skip(100).ToArray();
            }
        }

        void Finalizar(string reporte)
        {
            txtStats.Text = reporte;
            btnIniciar.Enabled = nudVel.Enabled = nudCap.Enabled = chkLenta.Enabled = true;
            btnIniciar.Text = "Reiniciar";
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FormPrincipal());
        }
    }
}