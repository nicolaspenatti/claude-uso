using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

class Sessao { public string Projeto, Chave; public long H5, D7; public DateTime Ultimo; public bool Nomeado; }
class Uso { public long H5, D7; public List<Sessao> Sessoes = new List<Sessao>(); }
class JanelaUso { public double Pct; public DateTime? Reinicia; } // Reinicia em UTC; null = sem janela ativa
class UsoOficial { public JanelaUso H5, D7; public DateTime Quando; }

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
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
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
        itemStatus = new ToolStripMenuItem("Consultando o uso oficial…") { Enabled = false };
        menu.Items.Add(itemStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Atualizar agora", null, (s, e) => { Atualizar(); AtualizarOficial(5); });
        menu.Items.Add("Limites da estimativa…", null, (s, e) => EditarLimites());
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
        timer.Tick += (s, e) => { if (watcher == null) IniciarWatcher(); Atualizar(); AtualizarOficial(BuscaMinSeg); };
        timer.Start();

        debounce = new System.Windows.Forms.Timer { Interval = 1500 };
        debounce.Tick += (s, e) => { debounce.Stop(); Atualizar(); AtualizarOficial(BuscaMinSeg); };
        buscaTimer = new System.Windows.Forms.Timer();
        buscaTimer.Tick += (s, e) => { buscaTimer.Stop(); AtualizarOficial(BuscaMinSeg); };
        IniciarWatcher();
        Atualizar();
        AtualizarOficial(0);
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
        buscaTimer.Stop();
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
        AtualizarOficial(15);
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
            var dica = new Label { Text = "Em milhões de tokens. Só vale sem os dados oficiais.", AutoSize = true, Location = new Point(pad, px(44)), ForeColor = TextoSuave, Font = new Font("Segoe UI", 8.5f) };

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

    // ---- Cálculo incremental (roda só na thread de Atualizar, uma por vez) ----
    class ArquivoCache { public long Offset; public string Chave; }
    class Registro { public DateTime Quando; public long Tokens; public string Chave; }

    readonly Dictionary<string, ArquivoCache> cacheArq = new Dictionary<string, ArquivoCache>();
    readonly List<Registro> registros = new List<Registro>();
    readonly Dictionary<string, DateTime> vistos = new Dictionary<string, DateTime>();
    readonly Dictionary<string, string> nomes = new Dictionary<string, string>();
    readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };

    void ZerarCache()
    {
        cacheArq.Clear(); registros.Clear(); vistos.Clear(); nomes.Clear();
    }

    Uso Calcular(DateTime corte5)
    {
        var r = new Uso();
        var corte7 = DateTime.UtcNow.AddDays(-7);
        string raiz = PastaProjetos();
        if (!Directory.Exists(raiz)) { ZerarCache(); return r; }

        var ultimos = new Dictionary<string, DateTime>(); // sessão -> última escrita
        var presentes = new HashSet<string>();
        foreach (var arq in Directory.EnumerateFiles(raiz, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                var escrito = File.GetLastWriteTimeUtc(arq);
                if (escrito < corte7) continue;
                presentes.Add(arq);
                ArquivoCache c;
                if (!cacheArq.TryGetValue(arq, out c))
                {
                    // sessão = projeto + id (subagentes ficam em subpastas e entram na sessão-mãe)
                    var partes = arq.Substring(raiz.Length).Trim('\\', '/').Split('\\', '/');
                    c = new ArquivoCache { Chave = partes[0] + "/" + (partes.Length > 1 ? Path.GetFileNameWithoutExtension(partes[1]) : "") };
                    cacheArq[arq] = c;
                }
                DateTime u;
                if (!ultimos.TryGetValue(c.Chave, out u) || escrito > u) ultimos[c.Chave] = escrito;
                if (!LerNovo(arq, c, corte7)) { ZerarCache(); return Calcular(corte5); } // arquivo truncado: recomeça do zero
            }
            catch { }
        }

        // esquece arquivos que sumiram ou saíram da janela
        var fora = new List<string>();
        foreach (var k in cacheArq.Keys) if (!presentes.Contains(k)) fora.Add(k);
        foreach (var k in fora) cacheArq.Remove(k);
        registros.RemoveAll(x => x.Quando < corte7);
        var velhos = new List<string>();
        foreach (var kv in vistos) if (kv.Value < corte7) velhos.Add(kv.Key);
        foreach (var k in velhos) vistos.Remove(k);

        var mapa = new Dictionary<string, Sessao>();
        foreach (var kv in ultimos)
        {
            string nome;
            bool nomeado = nomes.TryGetValue(kv.Key, out nome);
            var s = new Sessao { Chave = kv.Key, Ultimo = kv.Value, Nomeado = nomeado, Projeto = nomeado ? nome : kv.Key.Substring(0, kv.Key.IndexOf('/')) };
            mapa[kv.Key] = s;
            r.Sessoes.Add(s);
        }
        foreach (var x in registros)
        {
            Sessao sess;
            if (!mapa.TryGetValue(x.Chave, out sess)) continue;
            r.D7 += x.Tokens; sess.D7 += x.Tokens;
            if (x.Quando >= corte5) { r.H5 += x.Tokens; sess.H5 += x.Tokens; }
        }
        return r;
    }

    // lê só as linhas completas anexadas desde a última passada
    bool LerNovo(string arq, ArquivoCache c, DateTime corte7)
    {
        using (var fs = new FileStream(arq, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length < c.Offset) return false; // truncado ou reescrito
            if (fs.Length == c.Offset) return true;
            fs.Seek(c.Offset, SeekOrigin.Begin);
            var buf = new byte[81920];
            var acc = new MemoryStream();
            long pos = c.Offset;
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                int ini = 0;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != 10) continue;
                    acc.Write(buf, ini, i - ini);
                    ProcessarLinha(Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length), c, corte7);
                    pos += acc.Length + 1;
                    c.Offset = pos;
                    acc.SetLength(0);
                    ini = i + 1;
                }
                acc.Write(buf, ini, n - ini);
            }
            // o resto (linha ainda sem \n) fica para a próxima passada
        }
        return true;
    }

    void ProcessarLinha(string linha, ArquivoCache c, DateTime corte7)
    {
        if (linha.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) return;
        try
        {
            var o = ser.DeserializeObject(linha) as Dictionary<string, object>;
            if (o == null || !o.ContainsKey("timestamp")) return;
            object cwd;
            if (!nomes.ContainsKey(c.Chave) && o.TryGetValue("cwd", out cwd) && cwd != null)
            {
                string nome = Path.GetFileName(cwd.ToString().TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(nome)) nomes[c.Chave] = nome;
            }
            var msg = o["message"] as Dictionary<string, object>;
            if (msg == null) return;
            var u = msg["usage"] as Dictionary<string, object>;
            if (u == null) return;
            var quando = DateTime.Parse(o["timestamp"].ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            object id;
            if (msg.TryGetValue("id", out id) && id != null)
            {
                string sid = id.ToString();
                if (vistos.ContainsKey(sid)) return;
                vistos[sid] = quando;
            }
            if (quando < corte7) return;
            long n = Val(u, "input_tokens") + Val(u, "output_tokens") + Val(u, "cache_creation_input_tokens");
            registros.Add(new Registro { Quando = quando, Tokens = n, Chave = c.Chave });
        }
        catch { }
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
        var corte5 = CorteH5();
        Task.Run(() => Calcular(corte5)).ContinueWith(t =>
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

    // ---- Uso oficial: o mesmo endpoint que o Claude Code consulta para mostrar os limites do plano ----
    const string UrlUso = "https://api.anthropic.com/api/oauth/usage";
    const int BuscaMinSeg = 60;  // intervalo mínimo entre consultas automáticas
    const int ValidadeMin = 15;  // sem consulta bem-sucedida por esse tempo, volta para a estimativa

    class Resultado { public UsoOficial Dados; public string Erro; public int EsperarSeg; }

    UsoOficial dadosOficiais;
    bool buscando;
    DateTime ultimaBusca = DateTime.MinValue, bloqueadoAte = DateTime.MinValue;
    System.Windows.Forms.Timer buscaTimer; // consulta adiada até o fim do intervalo mínimo
    ToolStripMenuItem itemStatus;

    void AtualizarOficial(int idadeMinSeg)
    {
        if (buscando) return;
        var agora = DateTime.UtcNow;
        double espera = Math.Max((ultimaBusca.AddSeconds(idadeMinSeg) - agora).TotalSeconds, (bloqueadoAte - agora).TotalSeconds);
        if (espera > 0)
        {
            // adia para o fim do intervalo, para o valor não ficar defasado quando a atividade parar
            if (!buscaTimer.Enabled) { buscaTimer.Interval = (int)Math.Min(3600000, Math.Max(1000, espera * 1000)); buscaTimer.Start(); }
            return;
        }
        buscaTimer.Stop();
        buscando = true;
        ultimaBusca = agora;
        Task.Run(() => Consultar()).ContinueWith(t =>
        {
            buscando = false;
            if (IsDisposed) return;
            var r = t.IsFaulted ? new Resultado { Erro = "falha na consulta" } : t.Result;
            if (r.Dados != null)
            {
                dadosOficiais = r.Dados;
                itemStatus.Text = "Uso oficial · atualizado " + r.Dados.Quando.ToLocalTime().ToString("HH:mm");
            }
            else
            {
                if (r.EsperarSeg > 0) bloqueadoAte = DateTime.UtcNow.AddSeconds(r.EsperarSeg);
                var f = Fresco(); // o último valor oficial ainda vale por alguns minutos
                itemStatus.Text = f != null
                    ? "Uso oficial · último às " + f.Quando.ToLocalTime().ToString("HH:mm") + " (" + r.Erro + ")"
                    : "Estimativa local · " + r.Erro;
            }
            Atualizar(); // realinha a janela de 5h das sessões e repinta
            Invalidate();
            AtualizarIconeTray();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // só leitura: o app nunca renova nem grava o login; quem renova é o Claude Code
    static string LerToken(out string erro)
    {
        erro = "sem login do Claude Code";
        try
        {
            string arq = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(arq)) return null;
            string json;
            using (var fs = new FileStream(arq, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) json = sr.ReadToEnd();
            var raiz = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            object o, tok, exp;
            var oauth = raiz != null && raiz.TryGetValue("claudeAiOauth", out o) ? o as Dictionary<string, object> : null;
            if (oauth == null || !oauth.TryGetValue("accessToken", out tok) || tok == null) return null;
            if (oauth.TryGetValue("expiresAt", out exp) && exp != null)
            {
                var fim = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Convert.ToInt64(exp, CultureInfo.InvariantCulture));
                if (fim <= DateTime.UtcNow.AddSeconds(30)) { erro = "login expirado (use o Claude Code)"; return null; }
            }
            erro = null;
            return tok.ToString();
        }
        catch { erro = "não foi possível ler o login"; return null; }
    }

    // roda em thread do pool; não mexe em estado da janela
    static Resultado Consultar()
    {
        var r = new Resultado();
        string erro;
        string token = LerToken(out erro);
        if (token == null) { r.Erro = erro; return r; }
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(UrlUso);
            req.Timeout = req.ReadWriteTimeout = 10000;
            req.KeepAlive = false;
            req.UserAgent = "claude-uso-bandeja/1.0";
            req.Accept = "application/json";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                r.Dados = Interpretar(sr.ReadToEnd());
            if (r.Dados == null) r.Erro = "resposta inesperada da API";
        }
        catch (WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            int cod = resp == null ? 0 : (int)resp.StatusCode;
            if (resp == null) r.Erro = ex.Status == WebExceptionStatus.Timeout ? "tempo esgotado" : "sem conexão";
            else if (cod == 401 || cod == 403) r.Erro = "login recusado (use o Claude Code)";
            else if (cod == 429)
            {
                int seg;
                if (!int.TryParse(resp.Headers["Retry-After"], out seg)) seg = 300;
                r.EsperarSeg = Math.Max(60, Math.Min(3600, seg));
                r.Erro = "muitas consultas, aguardando";
            }
            else r.Erro = "erro " + cod + " da API";
            if (resp != null) resp.Close();
        }
        catch { r.Erro = "falha na consulta"; }
        return r;
    }

    static UsoOficial Interpretar(string json)
    {
        try
        {
            var raiz = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (raiz == null) return null;
            var h5 = LerJanela(raiz, "five_hour");
            var d7 = LerJanela(raiz, "seven_day");
            if (h5 == null || d7 == null) return null;
            return new UsoOficial { H5 = h5, D7 = d7, Quando = DateTime.UtcNow };
        }
        catch { return null; }
    }

    // campo ausente ou sem "utilization" = formato desconhecido (null); campo null = sem janela ativa (0%)
    static JanelaUso LerJanela(Dictionary<string, object> raiz, string chave)
    {
        object v;
        if (!raiz.TryGetValue(chave, out v)) return null;
        var j = new JanelaUso();
        var d = v as Dictionary<string, object>;
        if (d == null) return v == null ? j : null;
        object pct, fim;
        if (!d.TryGetValue("utilization", out pct) || pct == null) return null;
        j.Pct = Convert.ToDouble(pct, CultureInfo.InvariantCulture);
        if (d.TryGetValue("resets_at", out fim) && fim != null)
            j.Reinicia = DateTime.Parse(fim.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return j;
    }

    UsoOficial Fresco()
    {
        return dadosOficiais != null && (DateTime.UtcNow - dadosOficiais.Quando).TotalMinutes <= ValidadeMin ? dadosOficiais : null;
    }

    // janela já reiniciada = 0% até a próxima consulta
    static double PctReal(JanelaUso j)
    {
        if (j.Reinicia != null && DateTime.UtcNow >= j.Reinicia.Value) return 0;
        return Math.Max(0, Math.Min(100, j.Pct));
    }

    bool Pronto() { return carregado || Fresco() != null; }

    int PctH5() { var o = Fresco(); return o != null ? (int)Math.Round(PctReal(o.H5)) : carregado ? Pct(uso.H5, lim5h) : 0; }
    int PctD7() { var o = Fresco(); return o != null ? (int)Math.Round(PctReal(o.D7)) : carregado ? Pct(uso.D7, lim7d) : 0; }

    // com dados oficiais, a janela de 5h começa 5 h antes do reinício; sem eles, é a deslizante
    DateTime CorteH5()
    {
        var o = Fresco();
        if (o != null && o.H5.Reinicia != null && o.H5.Reinicia.Value > DateTime.UtcNow) return o.H5.Reinicia.Value.AddHours(-5);
        return DateTime.UtcNow.AddHours(-5);
    }

    // a API não separa por sessão: reparte a porcentagem oficial pelos tokens locais de cada uma
    int PctSessao(long tokens)
    {
        var o = Fresco();
        if (o == null) return Pct(tokens, lim5h);
        return uso.H5 > 0 ? (int)Math.Min(100, Math.Round(PctReal(o.H5) * tokens / uso.H5)) : 0;
    }

    static readonly string[] Dias = { "dom.", "seg.", "ter.", "qua.", "qui.", "sex.", "sáb." };

    // "em 4 h 40 min" (menos de 24 h) ou "qui., 18:00"; vazio sem janela ativa
    static string Reinicio(DateTime? fimUtc)
    {
        if (fimUtc == null) return "";
        var fim = fimUtc.Value.ToLocalTime();
        fim = new DateTime((fim.Ticks + 30 * TimeSpan.TicksPerSecond) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, fim.Kind); // minuto mais próximo
        var falta = fim - DateTime.Now;
        if (falta <= TimeSpan.Zero) return "";
        if (falta.TotalHours >= 24) return Dias[(int)fim.DayOfWeek] + ", " + fim.ToString("HH:mm", CultureInfo.InvariantCulture);
        int min = (int)Math.Ceiling(falta.TotalMinutes);
        return min < 60 ? "em " + min + " min" : "em " + (min / 60) + " h " + (min % 60) + " min";
    }

    string Detalhe(JanelaUso j, long tokens, long lim)
    {
        if (j == null) return carregado ? Fmt(tokens) + " / " + Fmt(lim) : "";
        string r = Reinicio(j.Reinicia);
        return r.Length > 0 ? "reinicia " + r : "sem uso recente";
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
            var of = Fresco();
            // cabeçalho
            TextRenderer.DrawText(g, "Claude", fMarca, new Point(pad, pad - (int)(4 * s)), Texto, TextFormatFlags.NoPadding);
            string hora = Pronto() ? (of != null ? of.Quando.ToLocalTime() : atualizadoEm).ToString("HH:mm") : "";
            int wh = Larg(g, hora, fMini);
            TextRenderer.DrawText(g, hora, fMini, new Point(pad + largura - wh, pad - (int)(2 * s)), TextoSuave, TextFormatFlags.NoPadding);

            var marcas = new List<string>();
            if (porSessao) marcas.Add("por sessão");
            if (of == null && carregado) marcas.Add("estimado");
            if (marcas.Count > 0)
                TextRenderer.DrawText(g, "· " + string.Join(" · ", marcas.ToArray()), fMini, new Point(pad + Larg(g, "Claude", fMarca) + (int)(6 * s), pad - (int)(2 * s)), TextoSuave, TextFormatFlags.NoPadding);

            int y = pad + (int)(26 * s);
            if (porSessao)
                PintarSessoes(g, s, y, pad, largura, fMini, fSessao, fDet);
            else
            {
                Bloco(g, s, "5 HORAS", Pronto(), PctH5(), Detalhe(of != null ? of.H5 : null, uso.H5, lim5h), y, pad, largura, fMini, fGrande, fSimbolo, fDet);
                Bloco(g, s, "7 DIAS", Pronto(), PctD7(), Detalhe(of != null ? of.D7 : null, uso.D7, lim7d), y + (int)(82 * s), pad, largura, fMini, fGrande, fSimbolo, fDet);
            }
        }
    }

    void Bloco(Graphics g, float s, string rotulo, bool ok, int pct, string det, int y, int pad, int largura, Font fMini, Font fGrande, Font fSimbolo, Font fDet)
    {
        var cor = CorPct(pct);

        TextoEspacado(g, rotulo, fMini, pad, y, TextoSuave, 1.2f * s);

        // número grande + símbolo %
        string num = ok ? pct.ToString() : "–";
        int wn = Larg(g, num, fGrande);
        int yn = y + (int)(10 * s);
        TextRenderer.DrawText(g, num, fGrande, new Point(pad - (int)(2 * s), yn), Texto, TextFormatFlags.NoPadding);
        if (ok) TextRenderer.DrawText(g, "%", fSimbolo, new Point(pad + wn, yn + (int)(14 * s)), TextoSuave, TextFormatFlags.NoPadding);

        // detalhe à direita, alinhado à base do número
        int wd = Larg(g, det, fDet);
        TextRenderer.DrawText(g, det, fDet, new Point(pad + largura - wd, yn + (int)(24 * s)), TextoSuave, TextFormatFlags.NoPadding);

        // barra fina
        int bh = (int)Math.Max(3, 4 * s), by = y + (int)(58 * s);
        var trilho = new RectangleF(pad, by, largura, bh);
        using (var p = Redondo(trilho, bh / 2f)) using (var b = new SolidBrush(Trilho)) g.FillPath(b, p);
        if (ok && pct > 0)
        {
            float w = Math.Max(bh, largura * pct / 100f);
            using (var p = Redondo(new RectangleF(pad, by, w, bh), bh / 2f)) using (var b = new SolidBrush(cor)) g.FillPath(b, p);
        }
    }

    void PintarSessoes(Graphics g, float s, int y, int pad, int largura, Font fMini, Font fSessao, Font fDet)
    {
        var of = Fresco();
        TextoEspacado(g, of != null ? "SESSÕES · JANELA DE 5 HORAS" : "SESSÕES · ÚLTIMAS 5 HORAS", fMini, pad, y, TextoSuave, 1.2f * s);
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
                int pct = PctSessao(x.H5);
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
        int pt = PctH5();
        TextRenderer.DrawText(g, "Total", fDet, new Point(pad, yr + (int)(10 * s)), TextoSuave, TextFormatFlags.NoPadding);
        string tot = carregado ? (of != null ? Fmt(uso.H5) : Fmt(uso.H5) + " / " + Fmt(lim5h)) + "  ·  " + pt + "%" : "";
        TextRenderer.DrawText(g, tot, fDet, new Point(pad + largura - Larg(g, tot, fDet), yr + (int)(10 * s)), Texto, TextFormatFlags.NoPadding);
    }

    DateTime atualizadoEm = DateTime.Now;

    // ---- Ícone da bandeja com a porcentagem ----
    void AtualizarIconeTray()
    {
        var of = Fresco();
        bool pronto = Pronto();
        int p5 = PctH5(), p7 = PctD7();
        string tip = "Claude — carregando…";
        if (of != null)
        {
            string r5 = Reinicio(of.H5.Reinicia);
            tip = string.Format("Claude — 5h: {0}%{1} | 7d: {2}%", p5, r5.Length > 0 ? " (reinicia " + r5 + ")" : "", p7);
        }
        else if (carregado)
            tip = string.Format("Claude — 5h: {0}% ({1}) | 7d: {2}% ({3}) · estimado", p5, Fmt(uso.H5), p7, Fmt(uso.D7));
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        int tam = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (var bmp = new Bitmap(tam, tam))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var p = Redondo(new RectangleF(0, 0, tam, tam), tam * 0.28f))
            using (var b = new SolidBrush(pronto ? CorPct(p5) : Trilho)) g.FillPath(b, p);
            string t = pronto ? (p5 >= 100 ? "!!" : p5.ToString()) : "…";
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
