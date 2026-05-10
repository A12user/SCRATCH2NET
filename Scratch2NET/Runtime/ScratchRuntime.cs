using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

// NOTE TO FUTURE SELF:
// Atlas optimisation hook lives in ScratchStage.LoadAssets().
// When ready: generate atlas on first run, save a manifest of md5ext→rect,
// checksum source files, regenerate if any changed, then load the atlas
// bitmap once instead of individual PNGs.

namespace Scratch2NET.Runtime
{
    // =========================================================================
    //  ScratchStage
    //  The WinForms Form that owns the game loop and renders all sprites.
    //  Generated projects inherit nothing from this — they call static Stage
    //  members and hold a reference to the single ScratchStage instance.
    // =========================================================================

    public class ScratchStage : Form
    {
        // ── Constants ─────────────────────────────────────────────────────────
        public const int ScratchWidth = 480;   // Scratch stage is 480×360
        public const int ScratchHeight = 360;

        // ── Fields ────────────────────────────────────────────────────────────
        private readonly Timer _gameTimer = new Timer();
        private readonly List<ScratchSprite> _sprites = new List<ScratchSprite>();
        private readonly object _spriteLock = new object();

        /// <summary>Folder that contains the asset files (md5ext filenames).</summary>
        public string AssetFolder { get; private set; }

        // Back-buffer bitmap — we paint everything here then Blt to the form.
        private Bitmap _backBuffer;
        private Graphics _backGraphics;

        // Keyboard state
        private readonly HashSet<Keys> _keysDown = new HashSet<Keys>();

        // ── Constructor ───────────────────────────────────────────────────────

        public ScratchStage(string assetFolder)
        {
            AssetFolder = assetFolder;

            // Form setup
            Text = "Scratch2NET";
            ClientSize = new Size(ScratchWidth, ScratchHeight);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            DoubleBuffered = true;
            BackColor = Color.White;

            // Back buffer
            _backBuffer = new Bitmap(ScratchWidth, ScratchHeight,
                                       PixelFormat.Format32bppArgb);
            _backGraphics = Graphics.FromImage(_backBuffer);
            _backGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            _backGraphics.SmoothingMode = SmoothingMode.AntiAlias;

            // Input events
            KeyDown += (s, e) => _keysDown.Add(e.KeyCode);
            KeyUp += (s, e) => _keysDown.Remove(e.KeyCode);
            KeyPreview = true;

            // Game timer — ~33ms ≈ 30 fps, matching Scratch's default frame rate
            _gameTimer.Interval = 33;
            _gameTimer.Tick += GameTick;
        }

        // ── Sprite registration ───────────────────────────────────────────────

        public void AddSprite(ScratchSprite sprite)
        {
            lock (_spriteLock)
                _sprites.Add(sprite);
        }

        public void RemoveSprite(ScratchSprite sprite)
        {
            lock (_spriteLock)
                _sprites.Remove(sprite);
        }

        /// <summary>
        /// Called by generated Program.cs after all sprites are added.
        /// Loads assets, sets initial state, fires OnFlagClicked on everything.
        /// </summary>
        public void StartProject()
        {
            lock (_spriteLock)
            {
                foreach (ScratchSprite s in _sprites)
                {
                    s.LoadAssets(AssetFolder);
                    s.InitialState();
                }
            }

            _gameTimer.Start();

            // Fire flag-clicked scripts concurrently (fire-and-forget;
            // they run on the UI thread via async state machines).
            lock (_spriteLock)
            {
                foreach (ScratchSprite s in _sprites.ToList())
                    _ = s.OnFlagClicked();
            }
        }

        // ── Game loop ─────────────────────────────────────────────────────────

        private void GameTick(object sender, EventArgs e)
        {
            // Update mouse state in Scratch coordinates
            Point mouse = PointToClient(Cursor.Position);
            UpdateMouseState(mouse);

            // Render
            RenderFrame();
            Invalidate();
        }

        private void UpdateMouseState(Point screenPos)
        {
            // Convert screen pixels to Scratch coordinates
            // Scratch: centre = (0,0), X right, Y up
            // Screen:  top-left = (0,0), X right, Y down
            double sx = screenPos.X - ScratchWidth / 2.0;
            double sy = ScratchHeight / 2.0 - screenPos.Y;

            // These are written to whatever static Stage class the generated
            // project defines.  We expose them via a static update method so
            // generated Stage.cs does not need to reference ScratchStage.
            StageMouseX = sx;
            StageMouseY = sy;
            StageMouseDown = (Control.MouseButtons & MouseButtons.Left) != 0;
        }

        // Static mouse/keyboard accessors — written here, read by Stage.cs
        public static double StageMouseX = 0;
        public static double StageMouseY = 0;
        public static bool StageMouseDown = false;

        public bool IsKeyPressed(Keys key) => _keysDown.Contains(key);

        // ── Rendering ─────────────────────────────────────────────────────────

        private void RenderFrame()
        {
            _backGraphics.Clear(Color.White);

            List<ScratchSprite> sorted;
            lock (_spriteLock)
                sorted = _sprites
                    .Where(s => s.Visible)
                    .OrderBy(s => s.LayerOrder)
                    .ToList();

            foreach (ScratchSprite sprite in sorted)
                sprite.Draw(_backGraphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(_backBuffer, 0, 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _gameTimer.Stop();
            ScratchSound.StopAll();
            base.OnFormClosed(e);
        }
    }

    // =========================================================================
    //  ScratchSprite
    //  Base class for every generated sprite class.
    //  Generated code overrides OnFlagClicked(), OnStartAsClone(), etc.
    // =========================================================================

    public abstract class ScratchSprite
    {
        // ── Position / transform (Scratch coordinate space) ───────────────────
        public double X { get; set; } = 0;
        public double Y { get; set; } = 0;
        /// <summary>Direction in Scratch degrees: 90 = right, 0 = up, -90 = left.</summary>
        public double Direction { get; set; } = 90;
        /// <summary>Size as a percentage (100 = normal).</summary>
        public new double Size { get; set; } = 100;
        public bool Visible { get; set; } = true;
        public int LayerOrder { get; set; } = 0;

        // ── Costumes ──────────────────────────────────────────────────────────
        protected readonly List<CostumeEntry> _costumes = new List<CostumeEntry>();
        public int CostumeIndex { get; set; } = 0;

        // ── Sounds ────────────────────────────────────────────────────────────
        private readonly Dictionary<string, string> _soundFiles
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Clone state ───────────────────────────────────────────────────────
        protected bool _cloneDeleted = false;
        private bool _isClone = false;

        // ── Stage reference ───────────────────────────────────────────────────
        protected ScratchStage _stage;

        // ── Constructor ───────────────────────────────────────────────────────

        protected ScratchSprite(ScratchStage stage)
        {
            _stage = stage;
        }

        // ── Asset registration (called from generated constructor) ────────────

        /// <summary>
        /// Registers a costume by friendly name and md5ext filename.
        /// rcx/rcy are the rotation centre in logical pixels.
        /// </summary>
        protected void AddCostume(string friendlyName, string md5ext,
                                   int rcx, int rcy)
        {
            _costumes.Add(new CostumeEntry
            {
                Name = friendlyName,
                Md5Ext = md5ext,
                RotCenterX = rcx,
                RotCenterY = rcy
            });
        }

        protected void AddSound(string friendlyName, string md5ext)
        {
            _soundFiles[friendlyName] = md5ext;
        }

        // ── Asset loading (called by ScratchStage.StartProject) ───────────────

        public void LoadAssets(string assetFolder)
        {
            // TODO (atlas optimisation): check for a pre-built atlas manifest
            // here before loading individual bitmaps.
            foreach (CostumeEntry c in _costumes)
            {
                string path = Path.Combine(assetFolder, c.Md5Ext);
                if (File.Exists(path))
                    c.Bitmap = new Bitmap(path);
                else
                    c.Bitmap = MakePlaceholderBitmap(c.Name);
            }
        }

        private static Bitmap MakePlaceholderBitmap(string label)
        {
            var bmp = new Bitmap(60, 60);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Magenta);
                g.DrawString(label, SystemFonts.DefaultFont,
                             Brushes.White, 2, 2);
            }
            return bmp;
        }

        // ── Override hooks (generated code fills these in) ────────────────────

        public virtual void InitialState() { }
        public virtual Task OnFlagClicked() => Task.CompletedTask;
        public virtual Task OnStartAsClone() => Task.CompletedTask;

        // ── Drawing ───────────────────────────────────────────────────────────

        public void Draw(Graphics g)
        {
            if (!Visible || CostumeIndex < 0 || CostumeIndex >= _costumes.Count)
                return;

            CostumeEntry c = _costumes[CostumeIndex];
            if (c.Bitmap == null) return;

            // Scale factor: Size is a percentage, bitmap may be @2x already
            // (rotationCenter was already halved in the transpiler, so we
            // treat the bitmap at face value here).
            double scale = Size / 100.0;
            int drawW = (int)(c.Bitmap.Width * scale);
            int drawH = (int)(c.Bitmap.Height * scale);

            // Convert Scratch coords to screen coords
            float screenX = (float)(ScratchStage.ScratchWidth / 2.0 + X);
            float screenY = (float)(ScratchStage.ScratchHeight / 2.0 - Y);

            GraphicsState state = g.Save();

            // Translate to sprite centre, rotate, translate back
            g.TranslateTransform(screenX, screenY);
            // Scratch direction: 90 = right (east). GDI+ angle: 0 = right.
            // Scratch 0 = up = GDI -90, so: gdiAngle = scratchDirection - 90
            g.RotateTransform((float)(Direction - 90));

            // Draw centred on rotation point
            int rcX = (int)(c.RotCenterX * scale);
            int rcY = (int)(c.RotCenterY * scale);
            g.DrawImage(c.Bitmap,
                new Rectangle(-rcX, -rcY, drawW, drawH));

            g.Restore(state);
        }

        // ── Motion blocks ─────────────────────────────────────────────────────

        public void MoveSteps(double steps)
        {
            // Scratch direction: 90 = right. Convert to standard math angle.
            double rad = (Direction - 90) * Math.PI / 180.0;
            X += steps * Math.Cos(rad);
            Y += steps * Math.Sin(-rad);   // Y flipped: positive = up in Scratch
        }

        public void TurnLeft(double degrees)
        {
            Direction = NormaliseAngle(Direction - degrees);
        }

        public void TurnRight(double degrees)
        {
            Direction = NormaliseAngle(Direction + degrees);
        }

        public void PointInDirection(double degrees)
        {
            Direction = NormaliseAngle(degrees);
        }

        public void PointTowards(string target)
        {
            double tx, ty;
            if (target == "_mouse_")
            {
                tx = ScratchStage.StageMouseX;
                ty = ScratchStage.StageMouseY;
            }
            else
            {
                // Future: look up another sprite by name
                return;
            }

            double dx = tx - X;
            double dy = ty - Y;
            // atan2 gives math angle; convert to Scratch direction
            double angle = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            Direction = NormaliseAngle(angle);
        }

        public void GoToXY(double x, double y)
        {
            X = x;
            Y = y;
        }

        public async Task GlideSecsToXY(double secs, double toX, double toY)
        {
            double startX = X;
            double startY = Y;
            int totalMs = (int)(secs * 1000);
            int steps = Math.Max(1, totalMs / 33); // one step per frame
            int delayMs = totalMs / steps;

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                X = startX + (toX - startX) * t;
                Y = startY + (toY - startY) * t;
                await Task.Delay(delayMs);
            }
            X = toX;
            Y = toY;
        }

        public void IfOnEdgeBounce()
        {
            // Get the sprite's approximate half-extents in Scratch units.
            // We use the current costume's bitmap size scaled by Size/100.
            double halfW = 0, halfH = 0;
            if (CostumeIndex >= 0 && CostumeIndex < _costumes.Count)
            {
                CostumeEntry c = _costumes[CostumeIndex];
                if (c.Bitmap != null)
                {
                    double scale = Size / 100.0;
                    halfW = c.Bitmap.Width * scale / 2.0;
                    halfH = c.Bitmap.Height * scale / 2.0;
                }
            }

            double stageHalfW = ScratchStage.ScratchWidth / 2.0;
            double stageHalfH = ScratchStage.ScratchHeight / 2.0;

            bool hitH = false, hitV = false;

            if (X + halfW > stageHalfW) { X = stageHalfW - halfW; hitH = true; }
            if (X - halfW < -stageHalfW) { X = -stageHalfW + halfW; hitH = true; }
            if (Y + halfH > stageHalfH) { Y = stageHalfH - halfH; hitV = true; }
            if (Y - halfH < -stageHalfH) { Y = -stageHalfH + halfH; hitV = true; }

            if (hitH || hitV)
            {
                // Reflect the direction vector
                double rad = (Direction - 90) * Math.PI / 180.0;
                double vx = Math.Cos(rad);
                double vy = -Math.Sin(rad);

                if (hitH) vx = -vx;
                if (hitV) vy = -vy;

                double newDir = Math.Atan2(-vy, vx) * 180.0 / Math.PI + 90;
                Direction = NormaliseAngle(newDir);
            }
        }

        // ── Looks blocks ──────────────────────────────────────────────────────

        public void SwitchCostume(int index)
        {
            if (index >= 0 && index < _costumes.Count)
                CostumeIndex = index;
        }

        public void SwitchCostume(double index)
            => SwitchCostume((int)index);

        public void SetSize(double percent)
        {
            Size = Math.Max(0, percent);
        }

        public void GoToFrontBack(string frontOrBack)
        {
            // Layer order is managed by ScratchStage based on the sprite list.
            // We just set a high or low layer number here; the stage sorts by it.
            if (frontOrBack == "front")
                LayerOrder = 9999;
            else
                LayerOrder = -9999;
        }

        // ── Clone blocks ──────────────────────────────────────────────────────

        public void CreateClone(ScratchSprite source)
        {
            // Deep-copy the source sprite's current state into a new instance
            // of the same runtime type, register it with the stage, and fire
            // OnStartAsClone().
            ScratchSprite clone = (ScratchSprite)MemberwiseClone();
            clone._isClone = true;
            clone._cloneDeleted = false;
            // Deep-copy costume list reference is fine — bitmaps are shared
            // (read-only after load).
            _stage.AddSprite(clone);
            _ = clone.OnStartAsClone();
        }

        /// <summary>Overload for "myself" — clones this sprite.</summary>
        public void CreateClone(object selfRef)
            => CreateClone(this);

        public void DeleteClone()
        {
            _cloneDeleted = true;
            _stage.RemoveSprite(this);
        }

        // ── Sound blocks ──────────────────────────────────────────────────────

        public void PlaySound(string name)
        {
            if (_soundFiles.TryGetValue(name, out string md5ext))
                ScratchSound.Play(Path.Combine(_stage.AssetFolder, md5ext));
        }

        public async Task PlaySoundUntilDone(string name)
        {
            if (_soundFiles.TryGetValue(name, out string md5ext))
                await ScratchSound.PlayUntilDone(
                    Path.Combine(_stage.AssetFolder, md5ext));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Normalises an angle to the range (-180, 180] as Scratch does.
        /// </summary>
        private static double NormaliseAngle(double degrees)
        {
            degrees = degrees % 360;
            if (degrees > 180) degrees -= 360;
            if (degrees <= -180) degrees += 360;
            return degrees;
        }
    }

    // =========================================================================
    //  CostumeEntry  —  runtime data for one costume
    // =========================================================================

    public class CostumeEntry
    {
        public string Name { get; set; }
        public string Md5Ext { get; set; }
        public Bitmap Bitmap { get; set; }
        public int RotCenterX { get; set; }
        public int RotCenterY { get; set; }
    }

    // =========================================================================
    //  ScratchSound  —  thin NAudio wrapper
    // =========================================================================

    public static class ScratchSound
    {
        private static readonly List<IWavePlayer> _players
            = new List<IWavePlayer>();

        public static void Play(string filePath)
        {
            if (!File.Exists(filePath)) return;

            var reader = new AudioFileReader(filePath);
            var player = new WaveOutEvent();
            player.Init(reader);

            player.PlaybackStopped += (s, e) =>
            {
                player.Dispose();
                reader.Dispose();
                lock (_players) _players.Remove(player);
            };

            lock (_players) _players.Add(player);
            player.Play();
        }

        public static async Task PlayUntilDone(string filePath)
        {
            if (!File.Exists(filePath)) return;

            var tcs = new TaskCompletionSource<bool>();
            var reader = new AudioFileReader(filePath);
            var player = new WaveOutEvent();
            player.Init(reader);

            player.PlaybackStopped += (s, e) =>
            {
                player.Dispose();
                reader.Dispose();
                tcs.TrySetResult(true);
            };

            player.Play();
            await tcs.Task;
        }

        public static void StopAll()
        {
            lock (_players)
            {
                foreach (IWavePlayer p in _players.ToList())
                {
                    try { p.Stop(); } catch { /* ignore */ }
                }
                _players.Clear();
            }
        }
    }

    // =========================================================================
    //  ScratchRuntime  —  static helpers called by generated code
    // =========================================================================

    public static class ScratchRuntime
    {
        private static readonly Random _rng = new Random();

        /// <summary>
        /// Converts a value to a number the way Scratch does:
        /// empty string → 0, non-numeric string → 0, bool → 0/1.
        /// </summary>
        public static double ToNumber(object value)
        {
            if (value == null) return 0;
            if (value is double d) return d;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is bool b) return b ? 1 : 0;
            if (double.TryParse(value.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed))
                return parsed;
            return 0;
        }

        /// <summary>
        /// Scratch equality: numeric if both sides parse as numbers,
        /// otherwise case-insensitive string comparison.
        /// </summary>
        public static bool Equals(object a, object b)
        {
            string sa = a?.ToString() ?? "";
            string sb = b?.ToString() ?? "";

            if (double.TryParse(sa,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double da) &&
                double.TryParse(sb,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double db))
                return da == db;

            return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        public static double PickRandom(double from, double to)
        {
            if (from > to) { double tmp = from; from = to; to = tmp; }
            // If both are integers, return a whole number like Scratch does.
            if (from == Math.Floor(from) && to == Math.Floor(to))
                return _rng.Next((int)from, (int)to + 1);
            return from + _rng.NextDouble() * (to - from);
        }

        public static double MathOp(string op, double n)
        {
            switch (op.ToLower())
            {
                case "abs": return Math.Abs(n);
                case "floor": return Math.Floor(n);
                case "ceiling":
                case "ceil": return Math.Ceiling(n);
                case "sqrt": return Math.Sqrt(n);
                case "sin": return Math.Round(Math.Sin(n * Math.PI / 180.0), 10);
                case "cos": return Math.Round(Math.Cos(n * Math.PI / 180.0), 10);
                case "tan": return Math.Round(Math.Tan(n * Math.PI / 180.0), 10);
                case "asin": return Math.Asin(n) * 180.0 / Math.PI;
                case "acos": return Math.Acos(n) * 180.0 / Math.PI;
                case "atan": return Math.Atan(n) * 180.0 / Math.PI;
                case "ln": return Math.Log(n);
                case "log": return Math.Log10(n);
                case "e ^": return Math.Pow(Math.E, n);
                case "10 ^": return Math.Pow(10, n);
                default: return n;
            }
        }

        public static string LetterOf(double index, object str)
        {
            string s = str?.ToString() ?? "";
            int i = (int)index - 1; // Scratch is 1-indexed
            if (i < 0 || i >= s.Length) return "";
            return s[i].ToString();
        }

        public static bool IsKeyPressed(string keyName)
        {
            // We delegate to the Win32 GetAsyncKeyState via the Forms key map.
            // This is a best-effort mapping of Scratch key names to Windows keys.
            Keys key = ScratchKeyToWinForms(keyName);
            if (key == Keys.None) return false;
            return (int)(GetAsyncKeyState((int)key) & 0x8000) != 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static Keys ScratchKeyToWinForms(string name)
        {
            switch (name?.ToLower())
            {
                case "space": return Keys.Space;
                case "left arrow": return Keys.Left;
                case "right arrow": return Keys.Right;
                case "up arrow": return Keys.Up;
                case "down arrow": return Keys.Down;
                case "enter": return Keys.Enter;
                case "a": return Keys.A;
                case "b": return Keys.B;
                case "c": return Keys.C;
                case "d": return Keys.D;
                case "e": return Keys.E;
                case "f": return Keys.F;
                case "g": return Keys.G;
                case "h": return Keys.H;
                case "i": return Keys.I;
                case "j": return Keys.J;
                case "k": return Keys.K;
                case "l": return Keys.L;
                case "m": return Keys.M;
                case "n": return Keys.N;
                case "o": return Keys.O;
                case "p": return Keys.P;
                case "q": return Keys.Q;
                case "r": return Keys.R;
                case "s": return Keys.S;
                case "t": return Keys.T;
                case "u": return Keys.U;
                case "v": return Keys.V;
                case "w": return Keys.W;
                case "x": return Keys.X;
                case "y": return Keys.Y;
                case "z": return Keys.Z;
                case "0": return Keys.D0;
                case "1": return Keys.D1;
                case "2": return Keys.D2;
                case "3": return Keys.D3;
                case "4": return Keys.D4;
                case "5": return Keys.D5;
                case "6": return Keys.D6;
                case "7": return Keys.D7;
                case "8": return Keys.D8;
                case "9": return Keys.D9;
                default: return Keys.None;
            }
        }
    }
}