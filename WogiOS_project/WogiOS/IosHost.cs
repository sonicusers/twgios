using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AVFoundation;
using CoreAnimation;
using Foundation;
using ObjCRuntime;
using OpenGLES;
using UIKit;
using WogShim;
using WogShim.Gpu;

namespace WogiOS;

// Android 의 AndroidHost(EGL + AudioTrack)에 해당하는 iOS 구현 (EAGL + AVAudioEngine)
internal sealed unsafe class IosHost : IHost
{
    const nuint GL_RENDERBUFFER = 0x8D41;
    const uint GL_FRAMEBUFFER = 0x8D40;
    const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    const uint GL_RB_WIDTH = 0x8D42, GL_RB_HEIGHT = 0x8D43;

    [DllImport("libSystem.dylib")] static extern nint dlopen(string path, int mode);
    [DllImport("libSystem.dylib")] static extern nint dlsym(nint handle, string name);

    delegate void BindFbDelegate(uint target, uint fb);

    readonly GameView _view;
    readonly ManualResetEventSlim _active = new(true);
    EAGLContext _ctx;
    nint _gles;
    uint _fbo, _rbo;
    int _w = 1, _h = 1;

    static BindFbDelegate _bindDel;
    static nint _bindThunk;
    static delegate* unmanaged<uint, uint, void> _origBind;
    static uint _defaultFbo;

    public IosHost(GameView view) { _view = view; }

    public Action FirstFrame { get; set; }
    public bool IsGles => true;
    public bool HasTouch => true;
    public bool ListDrives => false;
    public (int W, int H) SurfaceSize => (_w, _h);

    public void SetActive(bool on) { if (on) _active.Set(); else _active.Reset(); }

    // iOS 에는 기본 프레임버퍼(0)가 없으므로 glBindFramebuffer(…, 0) 을 우리 FBO 로 바꿔치기한다
    [MonoPInvokeCallback(typeof(BindFbDelegate))]
    static void BindFbThunk(uint target, uint fb) => _origBind(target, fb == 0 ? _defaultFbo : fb);

    public void StartGl()
    {
        _gles = dlopen("/System/Library/Frameworks/OpenGLES.framework/OpenGLES", 1);
        _ctx = new EAGLContext(EAGLRenderingAPI.OpenGLES3);
        if (!EAGLContext.SetCurrentContext(_ctx)) throw new InvalidOperationException("EAGLContext 생성 실패");

        _bindDel = BindFbThunk;
        _bindThunk = Marshal.GetFunctionPointerForDelegate(_bindDel);

        var genFb = (delegate* unmanaged<int, uint*, void>)dlsym(_gles, "glGenFramebuffers");
        var genRb = (delegate* unmanaged<int, uint*, void>)dlsym(_gles, "glGenRenderbuffers");
        var bindFb = (delegate* unmanaged<uint, uint, void>)dlsym(_gles, "glBindFramebuffer");
        var bindRb = (delegate* unmanaged<uint, uint, void>)dlsym(_gles, "glBindRenderbuffer");
        var fbRb = (delegate* unmanaged<uint, uint, uint, uint, void>)dlsym(_gles, "glFramebufferRenderbuffer");
        var rbParam = (delegate* unmanaged<uint, uint, int*, void>)dlsym(_gles, "glGetRenderbufferParameteriv");
        _origBind = bindFb;

        uint fb, rb;
        genFb(1, &fb); genRb(1, &rb);
        _fbo = fb; _rbo = rb; _defaultFbo = fb;
        bindFb(GL_FRAMEBUFFER, fb);
        bindRb((uint)GL_RENDERBUFFER, rb);
        if (!_ctx.RenderBufferStorage(GL_RENDERBUFFER, _view.EaglLayer))
            throw new InvalidOperationException("renderbufferStorage 실패");
        fbRb(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, (uint)GL_RENDERBUFFER, rb);
        int w, h;
        rbParam((uint)GL_RENDERBUFFER, GL_RB_WIDTH, &w);
        rbParam((uint)GL_RENDERBUFFER, GL_RB_HEIGHT, &h);
        _w = Math.Max(1, w); _h = Math.Max(1, h);
        Host.Log($"GLES surface {_w}x{_h}");

        GlLoader.Load(GetProcAddress);
    }

    public nint GetProcAddress(string name)
    {
        nint p = dlsym(_gles, name);
        if (name == "glBindFramebuffer") { _origBind = (delegate* unmanaged<uint, uint, void>)p; return _bindThunk; }
        return p;
    }

    public void SwapBuffers()
    {
        _active.Wait();
        var bindRb = (delegate* unmanaged<uint, uint, void>)dlsym(_gles, "glBindRenderbuffer");
        bindRb((uint)GL_RENDERBUFFER, _rbo);
        _ctx.PresentRenderBuffer(GL_RENDERBUFFER);
        var f = FirstFrame;
        if (f != null) { FirstFrame = null; f(); }
    }

    public void PumpEvents()
    {
        _active.Wait();
        WogPatcher.GameHooks.Refresh();
    }

    public void ShowMessage(string caption, string text) => Ask(caption, text, new[] { "확인" });

    public int Ask(string caption, string text, string[] buttons)
    {
        Host.LogSink("[ask] " + caption + "\n" + text + "\n[" + string.Join(" | ", buttons) + "]");
        if (NSThread.IsMain) return -1;
        int chosen = -1;
        using var done = new ManualResetEventSlim(false);
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var a = UIAlertController.Create(caption, text, UIAlertControllerStyle.Alert);
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                a.AddAction(UIAlertAction.Create(buttons[i], UIAlertActionStyle.Default, _ => { chosen = idx; done.Set(); }));
            }
            UIApplication.SharedApplication.KeyWindow?.RootViewController?.PresentViewController(a, true, null);
        });
        done.Wait();
        return chosen;
    }

    // ---- 폴더 선택 (파일 앱) ----
    const string BookmarkKey = "pickedFolderBookmarks";
    // 고른 폴더는 앱이 켜져 있는 동안 계속 읽을 수 있어야 하므로 접근 권한을 놓지 않고 들고 있는다
    static readonly List<NSUrl> Scoped = new();

    // 앱을 켤 때 지난번에 고른 폴더들의 접근 권한을 되살린다 (게임이 저장해 둔 경로가 계속 유효하도록)
    public static void RestorePickedFolders()
    {
        var saved = NSUserDefaults.StandardUserDefaults.StringForKey(BookmarkKey);
        if (string.IsNullOrEmpty(saved)) return;
        foreach (var b64 in saved.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var data = NSData.FromArray(Convert.FromBase64String(b64));
                var url = NSUrl.FromBookmarkData(data, (NSUrlBookmarkResolutionOptions)0, null, out bool stale, out NSError err);
                if (url != null && url.StartAccessingSecurityScopedResource()) Scoped.Add(url);
            }
            catch (FormatException) { }
        }
    }

    static void SaveBookmark(NSUrl url)
    {
        var bm = url.CreateBookmarkData((NSUrlBookmarkCreationOptions)0, null, null, out NSError err);
        if (bm == null) return;
        var d = NSUserDefaults.StandardUserDefaults;
        var list = new List<string>((d.StringForKey(BookmarkKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        list.Add(Convert.ToBase64String(bm.ToArray()));
        while (list.Count > 8) list.RemoveAt(0);
        d.SetString(string.Join("\n", list), BookmarkKey);
    }

    // ---- 원본 파트2 폴더 (게임의 3버튼 창을 iOS 에서 고를 수 없어서, 앱이 대신 기억해 두었다가 환경 변수로 알려 준다) ----
    const string Part2Key = "part2FolderBookmark";

    // 지난번에 고른 파트2 폴더의 접근 권한을 되살리고 경로를 돌려준다. 없으면 null
    public static string RestorePart2Folder()
    {
        var b64 = NSUserDefaults.StandardUserDefaults.StringForKey(Part2Key);
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var data = NSData.FromArray(Convert.FromBase64String(b64));
            var url = NSUrl.FromBookmarkData(data, (NSUrlBookmarkResolutionOptions)0, null, out bool stale, out NSError err);
            if (url != null && url.StartAccessingSecurityScopedResource())
            {
                Scoped.Add(url);
                return url.Path;
            }
        }
        catch (FormatException) { }
        return null;
    }

    static void SavePart2Bookmark(NSUrl url)
    {
        var bm = url.CreateBookmarkData((NSUrlBookmarkCreationOptions)0, null, null, out NSError err);
        if (bm == null) return;
        NSUserDefaults.StandardUserDefaults.SetString(Convert.ToBase64String(bm.ToArray()), Part2Key);
        NSUserDefaults.StandardUserDefaults.Synchronize();
    }

    // 게임 스레드에서 불린다. 안내 창을 띄운 뒤 파일 앱의 폴더 선택 화면을 열고, 고른 폴더 경로를 돌려준다. 취소하면 null
    // 게임이 "파트1 폴더"를 요청하면, PC 판처럼 파트2 / 파트1 중에서 고를 수 있게 버튼을 나눠 보여 준다.
    public string PickFolder(string description)
    {
        Host.LogSink("[pick] " + description);
        if (NSThread.IsMain) return null;
        string result = null;
        using var done = new ManualResetEventSlim(false);
        bool offerBoth = description != null && description.Contains("파트1");
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var root = UIApplication.SharedApplication.KeyWindow?.RootViewController;
            if (root == null) { done.Set(); return; }

            // 폴더(public.folder)를 고르는 파일 앱 화면
            void OpenPicker(string which)
            {
                var picker = new UIDocumentPickerViewController(new[] { "public.folder" }, UIDocumentPickerMode.Open);
                picker.AllowsMultipleSelection = false;
                picker.DidPickDocumentAtUrls += (s, e) =>
                {
                    var url = e.Urls != null && e.Urls.Length > 0 ? e.Urls[0] : null;
                    if (url != null && url.StartAccessingSecurityScopedResource())
                    {
                        Scoped.Add(url);
                        if (which == "part2")
                        {
                            // 게임은 이 시점에 이미 파트1 흐름이라 경로를 넘겨도 거절한다.
                            // 대신 저장해 두고, 다음 실행 때 WAROFGENESIS_ORIGINAL 로 알려 준다.
                            SavePart2Bookmark(url);
                            Host.LogSink("[pick] part2 saved -> " + url.Path);
                            var ok = UIAlertController.Create("파트2 폴더를 저장했어요",
                                "앱을 종료합니다. 다시 실행하면 이 폴더로 시작해요.", UIAlertControllerStyle.Alert);
                            ok.AddAction(UIAlertAction.Create("확인", UIAlertActionStyle.Default, _ => Environment.Exit(0)));
                            root.PresentViewController(ok, true, null);
                            return;
                        }
                        SaveBookmark(url);
                        result = url.Path;
                        Host.LogSink("[pick] " + which + " -> " + result);
                    }
                    done.Set();
                };
                picker.WasCancelled += (s, e) => done.Set();
                root.PresentViewController(picker, true, null);
            }

            string title = offerBoth ? "원본 폴더 선택" : "폴더 선택";
            string body = offerBoth
                ? "원본 파트2 가 있으면 파트2 폴더(TXR · Chr · Obs 가 든 곳)를,\n파트2 없이 파트1 만 있으면 파트1 폴더(Chars · Obss · Btls 가 든 곳)를 고르세요.\n\n원본 폴더에는 아무것도 쓰지 않습니다."
                : description;
            var alert = UIAlertController.Create(title, body, UIAlertControllerStyle.Alert);
            if (offerBoth)
            {
                alert.AddAction(UIAlertAction.Create("파트2 폴더 고르기", UIAlertActionStyle.Default, _ => OpenPicker("part2")));
                alert.AddAction(UIAlertAction.Create("파트1 폴더 고르기", UIAlertActionStyle.Default, _ => OpenPicker("part1")));
                alert.AddAction(UIAlertAction.Create("끝내기", UIAlertActionStyle.Cancel, _ => done.Set()));
            }
            else
            {
                alert.AddAction(UIAlertAction.Create("폴더 고르기", UIAlertActionStyle.Default, _ => OpenPicker("folder")));
                alert.AddAction(UIAlertAction.Create("취소", UIAlertActionStyle.Cancel, _ => done.Set()));
            }
            root.PresentViewController(alert, true, null);
        });
        done.Wait();
        return result;
    }

    public IAudioSink OpenAudio(int sampleRate, int channels, int bitsPerSample)
    {
        if ((bitsPerSample != 8 && bitsPerSample != 16) || channels < 1 || channels > 2) return null;
        return new AudioOut(sampleRate, channels, bitsPerSample);
    }

    sealed class EngineSink : IAudioSink
    {
        readonly AVAudioEngine _engine = new();
        readonly AVAudioPlayerNode _node = new();
        readonly AVAudioFormat _fmt;
        readonly int _ch, _bits, _frameBytes;
        long _scheduled, _played;
        int _gen;

        public EngineSink(int rate, int ch, int bits)
        {
            _ch = ch; _bits = bits; _frameBytes = ch * bits / 8;
            _fmt = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, rate, (uint)ch, false);
            _engine.AttachNode(_node);
            _engine.Connect(_node, _engine.MainMixerNode, _fmt);
            _engine.StartAndReturnError(out _);
            _node.Play();
        }

        public long PlayedFrames => Interlocked.Read(ref _played);

        public void Write(ReadOnlySpan<byte> pcm)
        {
            int frames = pcm.Length / _frameBytes;
            if (frames == 0) return;
            var buf = new AVAudioPcmBuffer(_fmt, (uint)frames) { FrameLength = (uint)frames };
            float** chans = (float**)buf.FloatChannelData;
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < _ch; c++)
                {
                    int o = (f * _ch + c) * (_bits / 8);
                    chans[c][f] = _bits == 16
                        ? (short)(pcm[o] | (pcm[o + 1] << 8)) / 32768f
                        : (pcm[o] - 128) / 128f;
                }
            int gen = Volatile.Read(ref _gen);
            Interlocked.Add(ref _scheduled, frames);
            _node.ScheduleBuffer(buf, () => { if (gen == Volatile.Read(ref _gen)) Interlocked.Add(ref _played, frames); });
        }

        public void Flush()
        {
            _node.Stop();
            Interlocked.Increment(ref _gen);
            Interlocked.Exchange(ref _played, Interlocked.Read(ref _scheduled));
            _node.Play();
        }

        public void Dispose() { _node.Stop(); _engine.Stop(); }
    }
}
