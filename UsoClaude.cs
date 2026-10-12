using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

class Sessao { public string Projeto, Chave; public long H5, D7; public DateTime Ultimo; public bool Nomeado; }
class Uso { public long H5, D7; public List<Sessao> Sessoes = new List<Sessao>(); }

class Painel : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    long lim5h = 5000000, lim7d = 50000000;
    Uso uso = new Uso();
    bool carregado;
    bool porSessao;
    NotifyIcon tray;
    System.Windows.Forms.Timer timer, debounce;
    FileSystemWatcher watcher;
    bool calculando, pendente;
    DateTime ultimoFechar = DateTime.MinValue;
    IntPtr ultimoHandle = IntPtr.Zero;

    static readonly Color Fundo = Color.FromArgb(24, 24, 27);
    static readonly Color Borda = Color.FromArgb(46, 46, 52);
    static readonly Color Trilho = Color.FromArgb(42, 42, 48);
    static readonly Color Texto = Color.FromArgb(242, 242, 245);
    static readonly Color TextoSuave = Color.FromArgb(150, 150, 162);
    static readonly Color Laranja = Color.FromArgb(217, 119, 87);
    static readonly Color Ambar = Color.FromArgb(233, 178, 70);
    static readonly Color Vermelho = Color.FromArgb(226, 90, 90);

    [STAThread]
    static void Main(string[] args)
    {
        bool novo;
        using (var mutex = new Mutex(true, "UsoClaudeBandeja", out novo))
        {
            if (!novo) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var p = new Painel();
            var h = p.Handle; // cria o handle sem mostrar a janela
            if (args.Length > 0 && args[0] == "--abrir") p.Abrir();
            if (args.Length > 0 && args[0] == "--limites") p.EditarLimites();
            Application.Run(new ApplicationContext());
        }
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // some do Alt+Tab
    }
    protected override bool ShowWithoutActivation { get { return false; } }

    public Painel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Fundo;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Uso do Claude";
        LerLimites();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Atualizar agora", null, (s, e) => Atualizar());
        menu.Items.Add("Limites…", null, (s, e) => EditarLimites());
        var intv = new ToolStripMenuItem("Verificação periódica");
        int atualSeg = IntervaloSeg();
        foreach (int seg in new[] { 60, 120, 300 })
        {
            var it = new ToolStripMenuItem(seg < 60 ? seg + " segundos" : seg == 60 ? "1 minuto" : (seg / 60) + " minutos") { Checked = seg == atualSeg };
            int segVal = seg;
            it.Click += (s, e) =>
            {
                foreach (ToolStripMenuItem o in intv.DropDownItems) o.Checked = (o == it);
                SalvarNum("Intervalo", segVal);
                timer.Interval = segVal * 1000;
            };
            intv.DropDownItems.Add(it);
        }
        menu.Items.Add(intv);
        porSessao = LerOpcao("PorSessao");
        var modo = new ToolStripMenuItem("Contabilizar por sessão") { Checked = porSessao, CheckOnClick = true };
        modo.CheckedChanged += (s, e) => { porSessao = modo.Checked; SalvarOpcao("PorSessao", porSessao); if (Visible) Reposicionar(); Invalidate(); };
        menu.Items.Add(modo);
        var auto = new ToolStripMenuItem("Iniciar com o Windows") { Checked = AutoIniciar(), CheckOnClick = true };
        auto.CheckedChanged += (s, e) => AutoIniciar(auto.Checked);
        menu.Items.Add(auto);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (s, e) => Sair());

        tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "Uso do Claude" };
        tray.MouseClick += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if ((DateTime.Now - ultimoFechar).TotalMilliseconds < 300) return; // o clique que fechou o painel
            Abrir();
        };
        AtualizarIconeTray();

        // fallback lento: a janela de 5h e a sessão "ativa" mudam com o tempo, sem evento de arquivo
        timer = new System.Windows.Forms.Timer { Interval = IntervaloSeg() * 1000 };
        timer.Tick += (s, e) => { if (watcher == null) IniciarWatcher(); Atualizar(); };
        timer.Start();

        debounce = new System.Windows.Forms.Timer { Interval = 1500 };
        debounce.Tick += (s, e) => { debounce.Stop(); Atualizar(); };
        IniciarWatcher();
        Atualizar();
    }

    static int IntervaloSeg() { return (int)Math.Max(60, LerNum("Intervalo", 120)); }

    static string PastaProjetos()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    }

    void IniciarWatcher()
    {
        try
        {
            string raiz = PastaProjetos();
            if (!Directory.Exists(raiz)) return;
            var w = new FileSystemWatcher(raiz, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024
            };
            FileSystemEventHandler h = (s, e) => AgendarAtualizacao();
            w.Changed += h; w.Created += h; w.Deleted += h;
            w.Renamed += (s, e) => AgendarAtualizacao();
            w.Error += (s, e) => AgendarAtualizacao(); // buffer estourou: recálculo completo
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch { watcher = null; }
    }

    // os eventos chegam em thread do pool; junta rajadas e volta para a thread da UI
    void AgendarAtualizacao()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() => { debounce.Stop(); debounce.Start(); }));
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int canto = 2; // cantos arredondados (Windows 11)
        DwmSetWindowAttribute(Handle, 33, ref canto, 4);
    }

    void Sair()
    {
        timer.Stop();
        debounce.Stop();
        if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
        tray.Visible = false;
        tray.Dispose();
        if (ultimoHandle != IntPtr.Zero) DestroyIcon(ultimoHandle);
        Application.Exit();
    }

    // ---- Painel flutuante ----
    const int MaxSessoes = 6;

    List<Sessao> SessoesVisiveis()
    {
        var l = new List<Sessao>();
        foreach (var x in uso.Sessoes) if (x.H5 > 0) l.Add(x);
        l.Sort((a, b) => b.H5.CompareTo(a.H5));
        return l;
    }

    void Reposicionar()
    {
        float s = DeviceDpi / 96f;
        int altura = 204;
        if (porSessao)
        {
            int n = SessoesVisiveis().Count;
            int linhas = Math.Min(n, MaxSessoes);
            altura = 22 + 26 + 22 + (n == 0 ? 36 : linhas * 40) + (n > MaxSessoes ? 20 : 0) + 50;
        }
        ClientSize = new Size((int)(290 * s), (int)(altura * s));
        var area = Screen.PrimaryScreen.WorkingArea;
        int m = (int)(10 * s);
        Location = new Point(area.Right - Width - m, area.Bottom - Height - m);
    }

    public void Abrir()
    {
        Reposicionar();
        Atualizar();
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible) { Hide(); ultimoFechar = DateTime.Now; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        base.OnKeyDown(e);
    }

    // ---- Dados ----
    static long LerNum(string nome, long padrao)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(ChaveApp))
            {
                var v = k == null ? null : k.GetValue(nome);
                return v == null ? padrao : Convert.ToInt64(v);
            }
        }
        catch { return padrao; }
    }

    static void SalvarNum(string nome, long v)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(ChaveApp)) k.SetValue(nome, v, RegistryValueKind.QWord); }
        catch { }
    }

    void LerLimites()
    {
        lim5h = LerNum("Limite5h", 5000000);
        lim7d = LerNum("Limite7d", 50000000);
        if (lim5h <= 0) lim5h = 5000000;
        if (lim7d <= 0) lim7d = 50000000;
    }

    static string Milhoes(long tokens) { return (tokens / 1e6).ToString("0.##", CultureInfo.InvariantCulture); }

    static bool LerMilhoes(string txt, out long tokens)
    {
        double v;
        tokens = 0;
        if (!double.TryParse(txt.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
        if (v < 0.01 || v > 100000) return false;
        tokens = (long)Math.Round(v * 1e6);
        return true;
    }

    public void EditarLimites()
    {
        using (var f = new Form())
        {
            float s = DeviceDpi / 96f;
            Func<int, int> px = v => (int)(v * s);
            f.Text = "Limites de uso";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.TopMost = true;
            f.StartPosition = FormStartPosition.Manual;
            f.BackColor = Fundo; f.ForeColor = Texto;
            f.Font = new Font("Segoe UI", 9f);
            f.ClientSize = new Size(px(330), px(226));
            f.HandleCreated += (a, b) => { var wa = Screen.PrimaryScreen.WorkingArea; f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + (wa.Height - f.Height) / 2); int escuro = 1; DwmSetWindowAttribute(f.Handle, 20, ref escuro, 4); };

            int pad = px(22), larg = px(330) - 2 * pad;
            var tit = new Label { Text = "Limites de uso", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Location = new Point(pad, px(18)), ForeColor = Texto };
            var dica = new Label { Text = "Em milhões de tokens. Ajuste conforme o seu plano.", AutoSize = true, Location = new Point(pad, px(44)), ForeColor = TextoSuave, Font = new Font("Segoe UI", 8.5f) };

            Func<string, int, long, TextBox> campo = (rotulo, y, valor) =>
            {
                f.Controls.Add(new Label { Text = rotulo, AutoSize = true, Location = new Point(pad, y), ForeColor = TextoSuave });
                var tb = new TextBox { Text = Milhoes(valor), Location = new Point(pad, y + px(20)), Width = px(110), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(36, 36, 42), ForeColor = Texto };
                f.Controls.Add(tb);
                f.Controls.Add(new Label { Text = "milhões de tokens", AutoSize = true, Location = new Point(pad + px(118), y + px(22)), ForeColor = TextoSuave });
                return tb;
            };
            var t5 = campo("Janela de 5 horas", px(78), lim5h);
            var t7 = campo("Janela de 7 dias", px(132), lim7d);

            var erro = new Label { Text = "", AutoSize = true, Location = new Point(pad, px(184)), ForeColor = Vermelho, Font = new Font("Segoe UI", 8.5f) };

            Func<string, Color, Button> botao = (txt, cor) =>
            {
                var b = new Button { Text = txt, FlatStyle = FlatStyle.Flat, BackColor = cor, ForeColor = Color.White, Height = px(30), Width = px(92), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0;
                return b;
            };
            var salvar = botao("Salvar", Laranja);
            var cancelar = botao("Cancelar", Color.FromArgb(52, 52, 60));
            var padrao = new Button { Text = "Restaurar", FlatStyle = FlatStyle.Flat, ForeColor = TextoSuave, BackColor = Fundo, Height = px(30), Width = px(80), Cursor = Cursors.Hand };
            padrao.FlatAppearance.BorderSize = 0;
            int yb = px(226) - px(30) - px(16);
            salvar.Location = new Point(pad + larg - px(92), yb);
            cancelar.Location = new Point(pad + larg - px(92) * 2 - px(8), yb);
            padrao.Location = new Point(pad - px(8), yb);
            padrao.Click += (a, b) => { t5.Text = Milhoes(5000000); t7.Text = Milhoes(50000000); erro.Text = ""; };
            salvar.Click += (a, b) =>
            {
                long n5, n7;
                if (!LerMilhoes(t5.Text, out n5) || !LerMilhoes(t7.Text, out n7)) { erro.Text = "Digite números válidos (ex.: 5 ou 0,5)."; return; }
                lim5h = n5; lim7d = n7;
                SalvarNum("Limite5h", n5); SalvarNum("Limite7d", n7);
                f.DialogResult = DialogResult.OK;
            };
            f.AcceptButton = salvar; f.CancelButton = cancelar;
            cancelar.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { tit, dica, erro, salvar, cancelar, padrao });

            if (f.ShowDialog() == DialogResult.OK) { Invalidate(); AtualizarIconeTray(); }
        }
    }

    static Uso Calcular()
    {
        var r = new Uso();
        var agora = DateTime.UtcNow;
        var corte7 = agora.AddDays(-7);
        var corte5 = agora.AddHours(-5);
        string raiz = PastaProjetos();
        if (!Directory.Exists(raiz)) return r;
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        var vistos = new HashSet<string>();
        var mapa = new Dictionary<string, Sessao>();
        foreach (var arq in Directory.EnumerateFiles(raiz, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                var escrito = File.GetLastWriteTimeUtc(arq);
                if (escrito < corte7) continue;
                // sessão = projeto + id (subagentes ficam em subpastas e entram na sessão-mãe)
                var partes = arq.Substring(raiz.Length).Trim('\\', '/').Split('\\', '/');
                string chave = partes[0] + "/" + (partes.Length > 1 ? Path.GetFileNameWithoutExtension(partes[1]) : "");
                Sessao sess;
                if (!mapa.TryGetValue(chave, out sess))
                {
                    sess = new Sessao { Chave = chave, Projeto = partes[0] };
                    mapa[chave] = sess;
                    r.Sessoes.Add(sess);
                }
                if (escrito > sess.Ultimo) sess.Ultimo = escrito;
                using (var fs = new FileStream(arq, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string linha;
                    while ((linha = sr.ReadLine()) != null)
                    {
                        if (linha.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;
                        try
                        {
                            var o = ser.DeserializeObject(linha) as Dictionary<string, object>;
                            if (o == null || !o.ContainsKey("timestamp")) continue;
                            object cwd;
                            if (!sess.Nomeado && o.TryGetValue("cwd", out cwd) && cwd != null)
                            {
                                string nome = Path.GetFileName(cwd.ToString().TrimEnd('\\', '/'));
                                if (!string.IsNullOrEmpty(nome)) { sess.Projeto = nome; sess.Nomeado = true; }
                            }
                            var msg = o["message"] as Dictionary<string, object>;
                            if (msg == null) continue;
                            var u = msg["usage"] as Dictionary<string, object>;
                            if (u == null) continue;
                            object id;
                            if (msg.TryGetValue("id", out id) && id != null && !vistos.Add(id.ToString())) continue;
                            var quando = DateTime.Parse(o["timestamp"].ToString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                            if (quando < corte7) continue;
                            long n = Val(u, "input_tokens") + Val(u, "output_tokens") + Val(u, "cache_creation_input_tokens");
                            r.D7 += n; sess.D7 += n;
                            if (quando >= corte5) { r.H5 += n; sess.H5 += n; }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        return r;
    }

    static long Val(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    }

    void Atualizar()
    {
        if (calculando) { pendente = true; return; }
        calculando = true;
        Task.Run(() => Calcular()).ContinueWith(t =>
        {
            calculando = false;
            if (IsDisposed) return;
            if (!t.IsFaulted)
            {
                uso = t.Result; carregado = true; atualizadoEm = DateTime.Now;
                if (Visible && porSessao) Reposicionar();
                Invalidate();
                AtualizarIconeTray();
            }
            if (pendente) { pendente = false; Atualizar(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Opções salvas ----
    const string ChaveApp = @"Software\UsoClaude";
    static bool LerOpcao(string nome)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(ChaveApp)) return k != null && Convert.ToInt32(k.GetValue(nome, 0)) == 1; }
        catch { return false; }
    }
    static void SalvarOpcao(string nome, bool v)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(ChaveApp)) k.SetValue(nome, v ? 1 : 0); }
        catch { }
    }

    // ---- Auto iniciar ----
    const string ChaveRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool AutoIniciar()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(ChaveRun)) return k != null && k.GetValue("UsoClaude") != null;
    }
    static void AutoIniciar(bool ligar)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(ChaveRun, true))
        {
            if (ligar) k.SetValue("UsoClaude", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("UsoClaude", false);
        }
    }

    // ---- Visual ----
    static int Pct(long v, long lim) { return (int)Math.Min(100, Math.Round(100.0 * v / lim)); }
    static Color CorPct(int p) { return p >= 85 ? Vermelho : p >= 60 ? Ambar : Laranja; }
    static string Fmt(long n)
    {
        if (n >= 1000000) return (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        if (n >= 1000) return (n / 1000).ToString("0", CultureInfo.InvariantCulture) + "k";
        return n.ToString();
    }

    static GraphicsPath Redondo(RectangleF r, float raio)
    {
        var p = new GraphicsPath();
        float d = Math.Min(raio * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void TextoEspacado(Graphics g, string t, Font f, float x, int y, Color c, float espaco)
    {
        foreach (char ch in t)
        {
            string s = ch.ToString();
            TextRenderer.DrawText(g, s, f, new Point((int)Math.Round(x), y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
            x += TextRenderer.MeasureText(g, s == " " ? "i" : s, f, new Size(1000, 100), TextFormatFlags.NoPadding).Width + espaco;
        }
    }

    static int Larg(Graphics g, string t, Font f)
    {
        return TextRenderer.MeasureText(g, t, f, new Size(1000, 100), TextFormatFlags.NoPadding).Width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Fundo);
        using (var pen = new Pen(Borda)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        int pad = (int)(22 * s);
        int largura = Width - 2 * pad;
        using (var fMarca = new Font("Segoe UI Semibold", 9.5f))
        using (var fMini = new Font("Segoe UI", 8f))
        using (var fGrande = new Font("Segoe UI Light", 26f))
        using (var fSimbolo = new Font("Segoe UI", 11f))
        using (var fDet = new Font("Segoe UI", 8.5f))
        using (var fSessao = new Font("Segoe UI", 9.5f))
        {
            // cabeçalho
            TextRenderer.DrawText(g, "Claude", fMarca, new Point(pad, pad - (int)(4 * s)), Texto, TextFormatFlags.NoPadding);
            string hora = carregado ? atualizadoEm.ToString("HH:mm") : "";
            int wh = Larg(g, hora, fMini);
            TextRenderer.DrawText(g, hora, fMini, new Point(pad + largura - wh, pad - (int)(2 * s)), TextoSuave, TextFormatFlags.NoPadding);

            int y = pad + (int)(26 * s);
            if (porSessao)
            {
                TextRenderer.DrawText(g, "· por sessão", fMini, new Point(pad + Larg(g, "Claude", fMarca) + (int)(6 * s), pad - (int)(2 * s)), TextoSuave, TextFormatFlags.NoPadding);
                PintarSessoes(g, s, y, pad, largura, fMini, fSessao, fDet);
            }
            else
            {
                Bloco(g, s, "5 HORAS", carregado ? uso.H5 : 0, lim5h, y, pad, largura, fMini, fGrande, fSimbolo, fDet);
                Bloco(g, s, "7 DIAS", carregado ? uso.D7 : 0, lim7d, y + (int)(82 * s), pad, largura, fMini, fGrande, fSimbolo, fDet);
            }
        }
    }

    void Bloco(Graphics g, float s, string rotulo, long v, long lim, int y, int pad, int largura, Font fMini, Font fGrande, Font fSimbolo, Font fDet)
    {
        int pct = Pct(v, lim);
        var cor = CorPct(pct);

        TextoEspacado(g, rotulo, fMini, pad, y, TextoSuave, 1.2f * s);

        // número grande + símbolo %
        string num = carregado ? pct.ToString() : "–";
        int wn = Larg(g, num, fGrande);
        int yn = y + (int)(10 * s);
        TextRenderer.DrawText(g, num, fGrande, new Point(pad - (int)(2 * s), yn), Texto, TextFormatFlags.NoPadding);
        if (carregado) TextRenderer.DrawText(g, "%", fSimbolo, new Point(pad + wn, yn + (int)(14 * s)), TextoSuave, TextFormatFlags.NoPadding);

        // detalhe à direita, alinhado à base do número
        string det = carregado ? Fmt(v) + " / " + Fmt(lim) : "";
        int wd = Larg(g, det, fDet);
        TextRenderer.DrawText(g, det, fDet, new Point(pad + largura - wd, yn + (int)(24 * s)), TextoSuave, TextFormatFlags.NoPadding);

        // barra fina
        int bh = (int)Math.Max(3, 4 * s), by = y + (int)(58 * s);
        var trilho = new RectangleF(pad, by, largura, bh);
        using (var p = Redondo(trilho, bh / 2f)) using (var b = new SolidBrush(Trilho)) g.FillPath(b, p);
        if (carregado && pct > 0)
        {
            float w = Math.Max(bh, largura * pct / 100f);
            using (var p = Redondo(new RectangleF(pad, by, w, bh), bh / 2f)) using (var b = new SolidBrush(cor)) g.FillPath(b, p);
        }
    }

    void PintarSessoes(Graphics g, float s, int y, int pad, int largura, Font fMini, Font fSessao, Font fDet)
    {
        TextoEspacado(g, "SESSÕES · ÚLTIMAS 5 HORAS", fMini, pad, y, TextoSuave, 1.2f * s);
        y += (int)(24 * s);
        var lista = SessoesVisiveis();
        if (!carregado || lista.Count == 0)
        {
            TextRenderer.DrawText(g, carregado ? "Nenhuma sessão ativa nas últimas 5h" : "Carregando…", fDet, new Point(pad, y + (int)(4 * s)), TextoSuave, TextFormatFlags.NoPadding);
            y += (int)(36 * s);
        }
        else
        {
            var contagem = new Dictionary<string, int>();
            foreach (var x in lista) { int c; contagem.TryGetValue(x.Projeto, out c); contagem[x.Projeto] = c + 1; }
            int n = Math.Min(lista.Count, MaxSessoes);
            for (int i = 0; i < n; i++)
            {
                var x = lista[i];
                string nome = x.Projeto;
                if (contagem[nome] > 1)
                {
                    string id = x.Chave.Substring(x.Chave.IndexOf('/') + 1);
                    nome += " · " + id.Substring(0, Math.Min(4, id.Length));
                }
                int pct = Pct(x.H5, lim5h);
                bool ativa = (DateTime.UtcNow - x.Ultimo).TotalMinutes < 2;

                int xt = pad;
                if (ativa)
                {
                    float d = 6 * s;
                    using (var b = new SolidBrush(Color.FromArgb(74, 200, 120))) g.FillEllipse(b, pad, y + 6 * s, d, d);
                    xt += (int)(13 * s);
                }
                string det = Fmt(x.H5) + "  ·  " + pct + "%";
                int wd = Larg(g, det, fDet);
                int maxNome = largura - wd - (xt - pad) - (int)(10 * s);
                TextRenderer.DrawText(g, nome, fSessao, new Rectangle(xt, y, maxNome, (int)(20 * s)), Texto,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, det, fDet, new Point(pad + largura - wd, y + (int)(2 * s)), TextoSuave, TextFormatFlags.NoPadding);

                int bh = (int)Math.Max(3, 3 * s), by = y + (int)(23 * s);
                using (var p = Redondo(new RectangleF(pad, by, largura, bh), bh / 2f)) using (var b = new SolidBrush(Trilho)) g.FillPath(b, p);
                if (x.H5 > 0)
                {
                    float w = Math.Max(bh, largura * pct / 100f);
                    using (var p = Redondo(new RectangleF(pad, by, w, bh), bh / 2f)) using (var b = new SolidBrush(CorPct(pct))) g.FillPath(b, p);
                }
                y += (int)(40 * s);
            }
            if (lista.Count > MaxSessoes)
            {
                TextRenderer.DrawText(g, "+" + (lista.Count - MaxSessoes) + " outras sessões", fDet, new Point(pad, y), TextoSuave, TextFormatFlags.NoPadding);
                y += (int)(20 * s);
            }
        }
        // rodapé: total geral
        int yr = y + (int)(6 * s);
        using (var pen = new Pen(Borda)) g.DrawLine(pen, pad, yr, pad + largura, yr);
        int pt = carregado ? Pct(uso.H5, lim5h) : 0;
        TextRenderer.DrawText(g, "Total", fDet, new Point(pad, yr + (int)(10 * s)), TextoSuave, TextFormatFlags.NoPadding);
        string tot = carregado ? Fmt(uso.H5) + " / " + Fmt(lim5h) + "  ·  " + pt + "%" : "";
        TextRenderer.DrawText(g, tot, fDet, new Point(pad + largura - Larg(g, tot, fDet), yr + (int)(10 * s)), Texto, TextFormatFlags.NoPadding);
    }

    DateTime atualizadoEm = DateTime.Now;

    // ---- Ícone da bandeja com a porcentagem ----
    void AtualizarIconeTray()
    {
        int p5 = carregado ? Pct(uso.H5, lim5h) : 0, p7 = carregado ? Pct(uso.D7, lim7d) : 0;
        string tip = carregado ? string.Format("Claude — 5h: {0}% ({1}) | 7d: {2}% ({3})", p5, Fmt(uso.H5), p7, Fmt(uso.D7)) : "Claude — carregando…";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        int tam = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (var bmp = new Bitmap(tam, tam))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var p = Redondo(new RectangleF(0, 0, tam, tam), tam * 0.28f))
            using (var b = new SolidBrush(carregado ? CorPct(p5) : Trilho)) g.FillPath(b, p);
            string t = carregado ? (p5 >= 100 ? "!!" : p5.ToString()) : "…";
            float fs = t.Length >= 3 ? tam * 0.46f : t.Length == 2 ? tam * 0.62f : tam * 0.78f;
            using (var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var sz = g.MeasureString(t, f);
                g.DrawString(t, f, Brushes.White, (tam - sz.Width) / 2f + 0.5f, (tam - sz.Height) / 2f + 0.5f);
            }
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (ultimoHandle != IntPtr.Zero) DestroyIcon(ultimoHandle);
            ultimoHandle = h;
        }
    }
}
