using System;
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

    // 폴더 선택 가져오기는 아직 없음: 파일 앱으로 Documents/assets/ 에 직접 넣어 주세요
    public string PickFolder(string description) => null;

    public IAudioSink OpenAudio(int sampleRate, int channels, int bitsPerSample)
    {
        if ((bitsPerSample != 8 && bitsPerSample != 16) || channels < 1 || channels > 2) return null;
        return new EngineSink(sampleRate, channels, bitsPerSample);
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
