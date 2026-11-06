using System;
using System.Globalization;
using System.Reflection;
using WogShim.Win32;

namespace WogiOS;

// 게임 안 [설정 > 게임 > 게임 속도] 와 "같은 값"을 쓰는 배속 도우미.
//  - 읽기: 게임의 DuelDx.GameWindow._gameSpeed (퍼센트 정수, 100 = 보통/원본)
//  - 쓰기: 설정 화면의 ◀▶ 와 똑같이 OnMenuCommand(1110 + 선택지 번호) 를 보낸다.
//          → 게임이 직접 값을 바꾸고, 설정 파일에 저장하고, "게임 속도: 1.5배" 안내까지 띄운다.
// WarOfGenesis.dll 의 GameWindow.SpeedChoices = { 100, 150, 200 } (보통(원본) · 1.5배 · 2배)
internal static class GameSpeed
{
    public static readonly int[] Choices = { 100, 150, 200 };
    const uint BaseCommand = 1110;   // GameWindow.OnMenuCommand 의 게임 속도 명령 시작 번호

    static FieldInfo _active;   // static  GameWindow _active  (지금 떠 있는 게임 창)
    static FieldInfo _speed;    // instance int _gameSpeed
    static bool _broken;
    static int _lastSent;     // 방금 보낸 배속 (게임이 처리하기 전에 또 눌러도 이어서 넘어가도록)
    static long _lastSentAt;

    static bool Bind()
    {
        if (_speed != null) return true;
        if (_broken) return false;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("DuelDx.GameWindow");
            if (t == null) continue;
            var a = t.GetField("_active", BindingFlags.NonPublic | BindingFlags.Static);
            var s = t.GetField("_gameSpeed", BindingFlags.NonPublic | BindingFlags.Instance);
            if (a == null || s == null) { _broken = true; return false; }   // 게임 버전이 바뀌어 이름이 달라진 경우
            _active = a; _speed = s;
            return true;
        }
        return false;   // 아직 게임이 안 떴다 → 다음에 다시 시도
    }

    // 현재 배속(퍼센트). 게임이 아직 안 떴거나 읽을 수 없으면 0
    public static int Current()
    {
        try
        {
            if (!Bind()) return 0;
            var game = _active.GetValue(null);
            return game == null ? 0 : (int)_speed.GetValue(game);
        }
        catch { return 0; }
    }

    // 보통 → 1.5배 → 2배 → 보통 … 순서로 한 칸 넘기고, 바꾼 배속(퍼센트)을 돌려준다
    public static int Next()
    {
        long now = Environment.TickCount64;
        int cur = now - _lastSentAt < 500 ? _lastSent : Current();   // 연타해도 한 칸씩 넘어간다
        int i = Array.IndexOf(Choices, cur);          // 목록에 없는 값이면 -1 → 보통(원본)부터
        int n = (i + 1) % Choices.Length;
        User32.RunMenuCommand(BaseCommand + (uint)n);
        _lastSent = Choices[n];
        _lastSentAt = now;
        return Choices[n];
    }

    public static string Label(int percent)
    {
        if (percent <= 0) percent = 100;
        return (percent / 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "x";   // 1x · 1.5x · 2x
    }
}
