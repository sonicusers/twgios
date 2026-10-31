using System;
using System.Collections.Generic;

namespace WogiOS;

// 안내줄 버튼(A/B/X/L)의 "속이 빈 흰 윤곽선" 그림을 만들어 주는 도우미.
// CapHook 이 게임의 GamePad.DrawCap 에서 이 메서드를 부른다. (게임 어셈블리에서 부르므로 public)
//   반환값: (w*Scale) x (h*Scale) 크기의 ARGB 배열. 윤곽선 부분만 불투명한 흰색, 나머지는 완전 투명(0).
//   같은 크기는 한 번만 만들어 두고 계속 같은 배열을 돌려준다 (게임이 배열 단위로 그림을 캐시하기 때문).
public static class CapRing
{
    public const int Scale = 4;       // 게임 화면 1픽셀을 몇 칸으로 그릴지
    public const int Thickness = 1;   // 윤곽선 두께 (게임 화면 기준 픽셀)
    const uint RingArgb = 0xFFF0F0F4; // 윤곽선 색(거의 흰색)

    static readonly Dictionary<long, uint[]> Cache = new Dictionary<long, uint[]>();

    // w, h, r : 알약의 가로/세로/모서리 반지름 (게임 화면 기준 픽셀)
    public static uint[] Get(int w, int h, int r)
    {
        if (w <= 0 || h <= 0) return new uint[1];
        if (r < 0) r = 0;
        long key = ((long)w << 42) ^ ((long)h << 21) ^ (long)r;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out uint[] hit)) return hit;
            uint[] made = Make(w * Scale, h * Scale, r * Scale);
            Cache[key] = made;
            return made;
        }
    }

    static uint[] Make(int W, int H, int R)
    {
        var data = new uint[W * H];
        double rad = Math.Min(R, Math.Min(W, H) / 2.0);
        double t = Thickness * Scale;
        double radIn = Math.Max(0, rad - t);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                if (Inside(px, py, 0, 0, W, H, rad) && !Inside(px, py, t, t, W - t, H - t, radIn))
                    data[y * W + x] = RingArgb;
            }
        }
        return data;
    }

    // (x0,y0)-(x1,y1) 둥근 사각형 안에 점이 있는지
    static bool Inside(double px, double py, double x0, double y0, double x1, double y1, double rad)
    {
        if (px < x0 || px > x1 || py < y0 || py > y1) return false;
        double cx = Math.Max(x0 + rad, Math.Min(px, x1 - rad));
        double cy = Math.Max(y0 + rad, Math.Min(py, y1 - rad));
        double dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= rad * rad;
    }
}
