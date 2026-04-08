using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using GLPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

class DummyGame : GameWindow
{
    // ===== WinAPI: hotkey + message pump + focus =====
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const int WM_HOTKEY = 0x0312;
    const uint PM_REMOVE = 0x0001;

    const uint MOD_SHIFT = 0x0004;
    const uint VK_TAB = 0x09;

    const int HOTKEY_ID_SHIFT_TAB = 1;

    const int SW_SHOW = 5;
    const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    IntPtr _hWnd = IntPtr.Zero;

    // ===== Shaders =====
    const string VertexShaderSource = @"#version 330 core
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
out vec2 TexCoord;
void main()
{
    gl_Position = vec4(aPosition, 1.0);
    TexCoord = vec2(aTexCoord.x, 1.0 - aTexCoord.y);
}";

    const string FragmentShaderSource = @"#version 330 core
out vec4 FragColor;
in vec2 TexCoord;
uniform sampler2D texture0;
void main()
{
    FragColor = texture(texture0, TexCoord);
}";

    int _vao, _vbo, _ebo, _program, _tex;

    readonly float[] _vertices =
    {
         // pos              // uv
         1.0f,  1.0f, 0.0f,   1.0f, 1.0f,
         1.0f, -1.0f, 0.0f,   1.0f, 0.0f,
        -1.0f, -1.0f, 0.0f,   0.0f, 0.0f,
        -1.0f,  1.0f, 0.0f,   0.0f, 1.0f
    };

    readonly uint[] _indices = { 0, 1, 3, 1, 2, 3 };

    // ===== Capture =====
    Bitmap _capture = null!;
    Graphics _graphics = null!;
    int _capW, _capH;

    double _captureTimer = 0;
    const double CaptureHz = 30.0;

    // ===== Show/Hide =====
    bool _isShownCenter = false;
    readonly Vector2i _shownSize = new Vector2i(1920, 1080);

    public DummyGame() : base(
        GameWindowSettings.Default,
        new NativeWindowSettings
        {
            Title = "GlosSITarget",
            Size = new Vector2i(1920, 1080),
            WindowBorder = WindowBorder.Resizable,
            WindowState = WindowState.Normal,
            StartVisible = true,
            API = ContextAPI.OpenGL
            // Profile satiri YOK: senin sürümde Compatibility yok diye kaldırdık
        })
    { }

    protected override void OnLoad()
    {
        base.OnLoad();

        // ✅ viewport
        GL.Viewport(0, 0, Size.X, Size.Y);

        GL.ClearColor(0f, 0f, 0f, 1f);

        _hWnd = Process.GetCurrentProcess().MainWindowHandle;

        MoveHidden();

        bool ok = RegisterHotKey(IntPtr.Zero, HOTKEY_ID_SHIFT_TAB, MOD_SHIFT, VK_TAB);
        if (!ok)
            Console.WriteLine("RegisterHotKey(SHIFT+TAB) basarisiz. GetLastError=" + Marshal.GetLastWin32Error());

        var primary = Screen.PrimaryScreen ?? throw new Exception("PrimaryScreen bulunamadi");
        _capW = primary.Bounds.Width;
        _capH = primary.Bounds.Height;

        _capture = new Bitmap(_capW, _capH, DrawingPixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_capture);

        SetupGl();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
    }

    void SetupGl()
    {
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), _vertices, BufferUsageHint.StaticDraw);

        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indices.Length * sizeof(uint), _indices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, VertexShaderSource);
        GL.CompileShader(vs);
        var vsLog = GL.GetShaderInfoLog(vs);
        if (!string.IsNullOrWhiteSpace(vsLog)) Console.WriteLine("VS LOG:\n" + vsLog);

        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, FragmentShaderSource);
        GL.CompileShader(fs);
        var fsLog = GL.GetShaderInfoLog(fs);
        if (!string.IsNullOrWhiteSpace(fsLog)) Console.WriteLine("FS LOG:\n" + fsLog);

        _program = GL.CreateProgram();
        GL.AttachShader(_program, vs);
        GL.AttachShader(_program, fs);
        GL.LinkProgram(_program);

        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
            Console.WriteLine("PROGRAM LINK LOG:\n" + GL.GetProgramInfoLog(_program));

        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        _tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _tex);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
            _capW, _capH, 0, GLPixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
    }

    void PumpWinMessagesForHotkey()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_HOTKEY)
            {
                int id = msg.wParam.ToInt32();
                if (id == HOTKEY_ID_SHIFT_TAB)
                    ToggleShowHide();
            }
        }
    }

    void EnsureHwnd()
    {
        if (_hWnd == IntPtr.Zero)
            _hWnd = Process.GetCurrentProcess().MainWindowHandle;
    }

    void BringToFrontAndFocus()
    {
        EnsureHwnd();
        if (_hWnd == IntPtr.Zero) return;

        ShowWindow(_hWnd, SW_RESTORE);
        ShowWindow(_hWnd, SW_SHOW);
        SetForegroundWindow(_hWnd);
        SetFocus(_hWnd);
    }

    void MoveHidden()
    {
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        int x = primary.Bounds.Left + 50;
        int y = primary.Bounds.Bottom + 20;

        WindowState = WindowState.Normal;
        Size = _shownSize;
        Location = new Vector2i(x, y);

        _isShownCenter = false;
    }

    void MoveCenterOnPrimary()
    {
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];

        int x = primary.Bounds.Left + (primary.Bounds.Width - _shownSize.X) / 2;
        int y = primary.Bounds.Top + (primary.Bounds.Height - _shownSize.Y) / 2;

        WindowState = WindowState.Normal;
        Size = _shownSize;
        Location = new Vector2i(x, y);

        _isShownCenter = true;
        BringToFrontAndFocus();
    }

    void ToggleShowHide()
    {
        if (_isShownCenter) MoveHidden();
        else MoveCenterOnPrimary();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        PumpWinMessagesForHotkey();

        if (KeyboardState.IsKeyPressed(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape))
            Close();

        bool allowRun = IsFocused || _isShownCenter;
        if (!allowRun) return;

        _captureTimer += args.Time;
        if (_captureTimer >= 1.0 / CaptureHz)
        {
            _captureTimer = 0;
            try
            {
                _graphics.CopyFromScreen(0, 0, 0, 0, new Size(_capW, _capH));
                UploadTexture();
            }
            catch { }
        }
    }

    void UploadTexture()
    {
        var data = _capture.LockBits(
            new Rectangle(0, 0, _capW, _capH),
            ImageLockMode.ReadOnly,
            DrawingPixelFormat.Format32bppArgb);

        GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, _capW, _capH,
            GLPixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);

        _capture.UnlockBits(data);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        if (!IsFocused && !_isShownCenter)
            return;

        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.BindTexture(TextureTarget.Texture2D, _tex);

        GL.DrawElements(PrimitiveType.Triangles, _indices.Length, DrawElementsType.UnsignedInt, 0);

        SwapBuffers();
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        try { UnregisterHotKey(IntPtr.Zero, HOTKEY_ID_SHIFT_TAB); } catch { }

        try { _graphics.Dispose(); } catch { }
        try { _capture.Dispose(); } catch { }

        try
        {
            GL.DeleteTexture(_tex);
            GL.DeleteProgram(_program);
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_ebo);
            GL.DeleteVertexArray(_vao);
        }
        catch { }
    }

    static void Main()
    {
        using var game = new DummyGame();
        game.Run();
    }
}
