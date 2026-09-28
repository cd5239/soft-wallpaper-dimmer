using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("柔光壁纸")]
[assembly: System.Reflection.AssemblyDescription("固定图片壁纸调暗与恢复工具")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

[Serializable]
public class SavedState
{
    public bool HasBackup;
    public string OriginalPath = "";
    public string OriginalStyle = "10";
    public string OriginalTile = "0";
    public string BackupPath = "";
    public string SourcePath = "";
    public string AppliedPath = "";
    public int Darkness = 25;
}

public class WallpaperInfo
{
    public string Path = "", Style = "10", Tile = "0";
}

public interface IWallpaper
{
    WallpaperInfo Read();
    void Set(string path, string style, string tile);
}

public sealed class WindowsWallpaper : IWallpaper
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    static extern bool GetWallpaper(uint action, uint size, StringBuilder value, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    static extern bool SetWallpaper(uint action, uint size, string value, uint flags);

    public WallpaperInfo Read()
    {
        var value = new StringBuilder(32768);
        if (!GetWallpaper(0x0073, (uint)value.Capacity, value, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前壁纸。");
        using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            return new WallpaperInfo { Path = value.ToString(),
                Style = key == null ? "10" : Convert.ToString(key.GetValue("WallpaperStyle", "10")),
                Tile = key == null ? "0" : Convert.ToString(key.GetValue("TileWallpaper", "0")) };
    }

    public void Set(string path, string style, string tile)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
        {
            if (key == null) throw new IOException("无法打开 Windows 壁纸设置。");
            object oldStyle = key.GetValue("WallpaperStyle"), oldTile = key.GetValue("TileWallpaper");
            try
            {
                key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
                key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
                if (!SetWallpaper(0x0014, 0, path, 3))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能应用壁纸。");
            }
            catch
            {
                if (oldStyle == null) key.DeleteValue("WallpaperStyle", false); else key.SetValue("WallpaperStyle", oldStyle);
                if (oldTile == null) key.DeleteValue("TileWallpaper", false); else key.SetValue("TileWallpaper", oldTile);
                throw;
            }
        }
    }
}

public static class Pictures
{
    public static Bitmap Load(string path)
    {
        using (var image = Image.FromFile(path))
        {
            // Respect camera orientation before flattening to a Windows wallpaper bitmap.
            if (Array.IndexOf(image.PropertyIdList, 0x0112) >= 0)
            {
                int o = image.GetPropertyItem(0x0112).Value[0];
                RotateFlipType[] rotations = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                    RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                    RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX,
                    RotateFlipType.Rotate270FlipNone };
                if (o >= 1 && o <= 8) image.RotateFlip(rotations[o]);
            }
            var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(copy))
            {
                g.Clear(Color.Black);
                g.DrawImage(image, new Rectangle(0, 0, copy.Width, copy.Height));
            }
            return copy;
        }
    }

    public static Bitmap Dim(Image source, int amount)
    {
        if (amount < 0 || amount > 75) throw new ArgumentOutOfRangeException("amount");
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(result))
        {
            g.DrawImageUnscaled(source, 0, 0);
            using (var brush = new SolidBrush(Color.FromArgb((int)Math.Round(amount * 255.0 / 100), Color.Black)))
                g.FillRectangle(brush, 0, 0, result.Width, result.Height);
        }
        return result;
    }
}

public sealed class Engine : IDisposable
{
    public readonly string Folder;
    public SavedState State;
    public Bitmap Source;
    public string SourceFile = "";
    public string SourceLabel = "尚未选择图片";
    readonly IWallpaper desktop;
    string statePath { get { return Path.Combine(Folder, "settings.xml"); } }

    public Engine(string folder, IWallpaper desktop)
    {
        Folder = folder; this.desktop = desktop;
        State = new SavedState();
        if (File.Exists(statePath))
        {
            using (var stream = File.OpenRead(statePath))
                State = (SavedState)new XmlSerializer(typeof(SavedState)).Deserialize(stream);
            State.Darkness = Math.Max(0, Math.Min(75, State.Darkness));
        }
    }

    void Save()
    {
        Directory.CreateDirectory(Folder);
        string temp = statePath + ".tmp";
        using (var stream = File.Create(temp)) new XmlSerializer(typeof(SavedState)).Serialize(stream, State);
        if (File.Exists(statePath)) File.Replace(temp, statePath, null); else File.Move(temp, statePath);
    }

    public void Select(string path)
    {
        var next = Pictures.Load(path);
        if (Source != null) Source.Dispose();
        Source = next; SourceFile = path; SourceLabel = path;
    }

    public void LoadCurrent()
    {
        var info = desktop.Read();
        // Always reload the pristine source when our own dimmed image is active.
        if (State.HasBackup && String.Equals(info.Path, State.AppliedPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(State.SourcePath))
        {
            Select(State.SourcePath); SourceLabel = "上次使用的原图（未调暗）"; return;
        }
        if (String.IsNullOrEmpty(info.Path)) throw new IOException("当前没有可读取的图片壁纸，请点击“选择图片”。");
        Select(info.Path);
    }

    public void Apply(int darkness)
    {
        if (Source == null) throw new InvalidOperationException("请先选择图片。");
        var current = desktop.Read();
        Directory.CreateDirectory(Folder);
        // Persist the original before the first system change, including a no-wallpaper state.
        if (!State.HasBackup)
        {
            string backup = "";
            if (!String.IsNullOrEmpty(current.Path))
            {
                backup = Path.Combine(Folder, "original-" + Guid.NewGuid().ToString("N") + ".bmp");
                using (var original = Pictures.Load(current.Path)) original.Save(backup, ImageFormat.Bmp);
            }
            State.OriginalPath = current.Path; State.OriginalStyle = current.Style; State.OriginalTile = current.Tile;
            State.BackupPath = backup; State.HasBackup = true;
            Save();
        }
        string id = Guid.NewGuid().ToString("N");
        string clean = Path.Combine(Folder, "source-" + id + ".png");
        string output = Path.Combine(Folder, "wallpaper-" + id + ".bmp");
        Source.Save(clean, ImageFormat.Png);
        using (var dimmed = Pictures.Dim(Source, darkness)) dimmed.Save(output, ImageFormat.Bmp);
        string previousOutput = State.AppliedPath, previousSource = State.SourcePath;
        int previousDarkness = State.Darkness;
        // Save recovery information before changing Windows, so a crash never loses the original.
        State.SourcePath = clean; State.AppliedPath = output; State.Darkness = darkness;
        Save();
        try { desktop.Set(output, current.Style, current.Tile); }
        catch
        {
            State.SourcePath = previousSource; State.AppliedPath = previousOutput; State.Darkness = previousDarkness;
            Save(); DeleteOwned(clean); DeleteOwned(output); throw;
        }
        DeleteOwned(previousOutput); DeleteOwned(previousSource);
    }

    public void Restore()
    {
        if (!State.HasBackup) throw new InvalidOperationException("还没有需要恢复的原壁纸。");
        string original = State.OriginalPath;
        if (!String.IsNullOrEmpty(original) && !File.Exists(original)) original = State.BackupPath;
        if (!String.IsNullOrEmpty(State.OriginalPath) && !File.Exists(original))
            throw new IOException("原壁纸及恢复副本均不可用，未更改桌面。");
        desktop.Set(original, State.OriginalStyle, State.OriginalTile);
        string lastOutput = State.AppliedPath, lastSource = State.SourcePath, backup = State.BackupPath;
        State.HasBackup = false; State.AppliedPath = ""; State.SourcePath = "";
        Save();
        DeleteOwned(lastOutput); DeleteOwned(lastSource);
        // Keep a fallback that Windows is actively using.
        if (!String.Equals(original, backup, StringComparison.OrdinalIgnoreCase)) DeleteOwned(backup);
    }

    void DeleteOwned(string path)
    {
        if (String.IsNullOrEmpty(path)) return;
        try
        {
            if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase)) return;
            string name = Path.GetFileName(path);
            if (name.StartsWith("wallpaper-") || name.StartsWith("source-") || name.StartsWith("original-")) File.Delete(path);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { if (Source != null) Source.Dispose(); }
}

public sealed class Preview : Control
{
    public Image Source;
    public int Darkness = 25;
    public bool ShowOriginal;
    public Preview() { DoubleBuffered = true; BackColor = Color.FromArgb(15, 20, 28); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Source == null)
        {
            TextRenderer.DrawText(e.Graphics, "选择一张图片，预览柔和的桌面", Font, ClientRectangle,
                Color.FromArgb(150, 163, 182), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        float scale = Math.Min((float)Width / Source.Width, (float)Height / Source.Height);
        int w = (int)(Source.Width * scale), h = (int)(Source.Height * scale);
        var rect = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(Source, rect);
        using (var brush = new SolidBrush(Color.FromArgb(ShowOriginal ? 0 : (int)Math.Round(Darkness * 255.0 / 100), Color.Black)))
            e.Graphics.FillRectangle(brush, rect);
        using (var brush = new SolidBrush(Color.FromArgb(180, 15, 20, 28)))
            e.Graphics.FillRectangle(brush, 12, 12, 128, 30);
        TextRenderer.DrawText(e.Graphics, ShowOriginal ? "原图 · 对比中" : "调暗预览 · " + Darkness + "%", Font,
            new Rectangle(15, 13, 124, 28), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

public sealed class MainForm : Form
{
    readonly Engine engine;
    readonly Preview preview = new Preview();
    readonly TrackBar slider = new TrackBar();
    readonly Label amount = new Label(), file = new Label(), status = new Label();
    readonly Button apply, restore, export;
    readonly Color muted = Color.FromArgb(164, 177, 196);

    Label LabelAt(string text, int x, int y, int w, int h, float size, Color color)
    {
        var l = new Label { Text = text, Bounds = new Rectangle(x, y, w, h), ForeColor = color,
            Font = new Font("Microsoft YaHei UI", size), AutoEllipsis = true };
        Controls.Add(l); return l;
    }

    Button ButtonAt(string text, int x, int y, int w, bool primary, EventHandler action)
    {
        var b = new Button { Text = text, Bounds = new Rectangle(x, y, w, 38), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(107, 220, 186) : Color.FromArgb(42, 54, 71),
            ForeColor = primary ? Color.FromArgb(13, 38, 32) : Color.White, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0; b.Click += action; Controls.Add(b); return b;
    }

    public MainForm(Engine engine, bool initialize)
    {
        this.engine = engine;
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "柔光壁纸 · Wallpaper Dimmer";
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(24, 32, 44); ForeColor = Color.White;
        ClientSize = new Size(800, 690); FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        LabelAt("柔光壁纸", 28, 22, 320, 40, 23, Color.White);
        LabelAt("让桌面柔和一点。", 30, 69, 680, 24, 10, muted);
        ButtonAt("读取当前壁纸", 28, 108, 150, false, delegate { Run(delegate { engine.LoadCurrent(); RefreshSource(); SetStatus("已读取原图，调整滑块即可预览。"); }); });
        ButtonAt("选择图片…", 190, 108, 136, false, delegate { Choose(); });
        var compare = ButtonAt("按住看原图", 636, 108, 136, false, delegate { });
        compare.MouseDown += delegate { preview.ShowOriginal = true; preview.Invalidate(); };
        compare.MouseUp += delegate { preview.ShowOriginal = false; preview.Invalidate(); };
        compare.MouseCaptureChanged += delegate { preview.ShowOriginal = false; preview.Invalidate(); };
        compare.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Space) { preview.ShowOriginal = true; preview.Invalidate(); } };
        compare.KeyUp += delegate { preview.ShowOriginal = false; preview.Invalidate(); };
        preview.Bounds = new Rectangle(28, 160, 744, 300); Controls.Add(preview);
        file.Bounds = new Rectangle(28, 470, 744, 24); file.ForeColor = muted; file.AutoEllipsis = true; Controls.Add(file);
        LabelAt("调暗程度", 28, 510, 135, 28, 11, Color.White);
        amount.Bounds = new Rectangle(688, 510, 84, 28); amount.TextAlign = ContentAlignment.TopRight;
        amount.ForeColor = Color.FromArgb(107, 220, 186); amount.Font = new Font(Font.FontFamily, 13F); Controls.Add(amount);
        slider.Bounds = new Rectangle(22, 543, 756, 40); slider.Minimum = 0; slider.Maximum = 75;
        slider.TickFrequency = 5; slider.SmallChange = 1; slider.LargeChange = 5; slider.Value = engine.State.Darkness;
        slider.AccessibleName = "调暗程度，百分比"; slider.ValueChanged += delegate { UpdateAmount(); }; Controls.Add(slider);
        apply = ButtonAt("应用到桌面", 28, 594, 155, true, delegate { Run(delegate { engine.Apply(slider.Value); RefreshButtons(); SetStatus("已应用。关闭工具后效果仍保留，可随时重新打开恢复。"); }); });
        restore = ButtonAt("恢复原壁纸", 195, 594, 145, false, delegate { Run(delegate { engine.Restore(); RefreshButtons(); SetStatus("已恢复首次应用前的壁纸及显示方式。"); }); });
        export = ButtonAt("另存调暗图片…", 352, 594, 160, false, delegate { Export(); });
        LabelAt("仅调暗壁纸 · 无需后台运行", 540, 604, 232, 24, 9, muted);
        status.Bounds = new Rectangle(28, 649, 744, 28); status.ForeColor = muted; status.AutoEllipsis = true; Controls.Add(status);
        SetStatus("适用于固定图片；建议从 25% 开始。应用会将各屏幕设为同一张图片。");
        if (initialize)
        {
            try { engine.LoadCurrent(); }
            catch { SetStatus("未能读取当前图片，请点击“选择图片”。"); }
        }
        RefreshSource(); UpdateAmount();
    }

    void RefreshSource() { preview.Source = engine.Source; file.Text = engine.SourceLabel; preview.Invalidate(); RefreshButtons(); }
    void RefreshButtons() { apply.Enabled = export.Enabled = engine.Source != null; restore.Enabled = engine.State.HasBackup; }
    void UpdateAmount() { amount.Text = slider.Value + "%"; preview.Darkness = slider.Value; preview.Invalidate(); }
    void SetStatus(string text) { status.Text = text; }
    void Run(Action action)
    {
        try { UseWaitCursor = true; action(); }
        catch (Exception ex) { RefreshButtons(); SetStatus("操作未完成：" + ex.Message); MessageBox.Show(this, ex.Message, "柔光壁纸", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { UseWaitCursor = false; }
    }
    void Choose()
    {
        using (var dialog = new OpenFileDialog { Title = "选择原始壁纸图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*" })
            if (dialog.ShowDialog(this) == DialogResult.OK)
                Run(delegate { engine.Select(dialog.FileName); RefreshSource(); SetStatus("原文件保持不变；点击“应用到桌面”后生效。"); });
    }
    void Export()
    {
        using (var dialog = new SaveFileDialog { Title = "另存调暗图片", Filter = "PNG 图片|*.png", FileName = "柔光壁纸-" + slider.Value + "%.png", DefaultExt = "png" })
            if (dialog.ShowDialog(this) == DialogResult.OK)
                Run(delegate {
                    if (String.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(engine.SourceFile), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("请使用其他文件名，保留原图。");
                    using (var dimmed = Pictures.Dim(engine.Source, slider.Value)) dimmed.Save(dialog.FileName, ImageFormat.Png);
                    SetStatus("调暗图片已保存，桌面未更改。");
                });
    }
}

public sealed class FakeWallpaper : IWallpaper
{
    public WallpaperInfo Current = new WallpaperInfo();
    public bool Fail;
    public WallpaperInfo Read() { return new WallpaperInfo { Path = Current.Path, Style = Current.Style, Tile = Current.Tile }; }
    public void Set(string path, string style, string tile)
    {
        if (Fail) throw new IOException("模拟系统拒绝修改");
        Current = new WallpaperInfo { Path = path, Style = style, Tile = tile };
    }
}

public static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread]
    public static int Main(string[] args)
    {
        SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--self-test") return Tests(args[1]);
        bool created;
        using (var mutex = new Mutex(true, "Local\\SoftWallpaperDimmer", out created))
        {
            if (!created) { MessageBox.Show("柔光壁纸已经打开，请查看任务栏。", "柔光壁纸"); return 0; }
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftWallpaperDimmer");
                using (var engine = new Engine(folder, new WindowsWallpaper()))
                using (var form = new MainForm(engine, true)) Application.Run(form);
                return 0;
            }
            catch (Exception ex) { MessageBox.Show("启动失败，现有恢复文件保持不变。\n\n" + ex.Message, "柔光壁纸", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }

    static void Assert(bool pass, string message) { if (!pass) throw new Exception(message); }
    static int Tests(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            string original = Path.Combine(folder, "test-original.png"), chosen = Path.Combine(folder, "test-selected.png");
            using (var image = new Bitmap(640, 360))
            {
                using (var g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(200, 160, 120));
                image.Save(original, ImageFormat.Png);
                using (var d = Pictures.Dim(image, 25)) Assert(Math.Abs(d.GetPixel(5, 5).R - 150) <= 1, "25% pixel math");
                using (var d = Pictures.Dim(image, 0)) Assert(d.GetPixel(5, 5).R == 200, "0% unchanged");
                using (var d = Pictures.Dim(image, 75)) Assert(Math.Abs(d.GetPixel(5, 5).R - 50) <= 1, "75% pixel math");
                using (var g = Graphics.FromImage(image))
                using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, 640, 360), Color.FromArgb(61, 110, 136), Color.FromArgb(229, 184, 139), 35F))
                {
                    g.FillRectangle(gradient, 0, 0, 640, 360);
                    using (var hill = new SolidBrush(Color.FromArgb(70, 92, 90))) g.FillEllipse(hill, -120, 200, 630, 350);
                    using (var hill = new SolidBrush(Color.FromArgb(38, 62, 68))) g.FillEllipse(hill, 290, 230, 600, 380);
                }
                image.Save(chosen, ImageFormat.Png);
            }
            byte[] originalBytes = File.ReadAllBytes(original);
            var desktop = new FakeWallpaper { Current = new WallpaperInfo { Path = original, Style = "6", Tile = "0" } };
            string settings = Path.Combine(folder, "state");
            string backup;
            using (var engine = new Engine(settings, desktop))
            {
                engine.LoadCurrent(); engine.Apply(25);
                Assert(File.Exists(desktop.Current.Path), "applied file exists");
                using (var image = Pictures.Load(desktop.Current.Path)) Assert(Math.Abs(image.GetPixel(5, 5).R - 150) <= 1, "applied pixels");
                engine.LoadCurrent(); engine.Apply(50);
                using (var image = Pictures.Load(desktop.Current.Path)) Assert(Math.Abs(image.GetPixel(5, 5).R - 100) <= 1, "no cumulative dimming");
                Assert(engine.State.OriginalPath == original, "first original retained");
                backup = engine.State.BackupPath;
                desktop.Fail = true; string previous = desktop.Current.Path;
                try { engine.Apply(30); throw new Exception("missing failure"); } catch (IOException) { }
                Assert(engine.State.AppliedPath == previous && desktop.Current.Path == previous, "failure rollback");
                desktop.Fail = false;
            }
            using (var engine = new Engine(settings, desktop))
            {
                Assert(engine.State.HasBackup, "backup persists across restart");
                engine.LoadCurrent(); Assert(engine.Source.GetPixel(5, 5).R == 200, "pristine source persists");
                engine.Restore(); Assert(desktop.Current.Path == original && desktop.Current.Style == "6", "restore image and style");
                Assert(!engine.State.HasBackup, "restore clears active state");
                engine.Select(chosen); engine.Apply(25);
                File.Move(original, original + ".moved");
                engine.Restore(); Assert(File.Exists(desktop.Current.Path), "missing original fallback");
                using (var image = Pictures.Load(desktop.Current.Path)) Assert(image.GetPixel(5, 5).R == 200, "fallback pixels");
                Assert(Convert.ToBase64String(originalBytes) == Convert.ToBase64String(File.ReadAllBytes(original + ".moved")), "original bytes preserved");
                File.Move(original + ".moved", original);
                engine.Select(chosen);
                using (var form = new MainForm(engine, false))
                {
                    form.Show(); Application.DoEvents();
                    using (var shot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size)); shot.Save(Path.Combine(folder, "ui-preview.png"), ImageFormat.Png); }
                    form.Close();
                }
            }
            var blank = new FakeWallpaper();
            using (var engine = new Engine(Path.Combine(folder, "blank-state"), blank))
            {
                engine.Select(chosen); engine.Apply(25); engine.Restore(); Assert(blank.Current.Path == "", "restore no-image wallpaper");
            }
            File.WriteAllText(Path.Combine(folder, "test-result.txt"), "PASS: 16 checks; image math, unchanged originals, non-cumulative dimming, persistence, failure rollback, original and fallback restore, blank wallpaper restore, UI render. No real wallpaper was changed.", Encoding.UTF8);
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "test-result.txt"), ex.ToString(), Encoding.UTF8); return 1; }
    }
}
