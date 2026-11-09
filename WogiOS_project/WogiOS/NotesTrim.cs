using System;
using System.Text;

namespace WogiOS;

// 게임 메뉴 「개발 > 패치 내역」은 릴리즈노트 전체(100여 판, 4만 자)를 한꺼번에 줄바꿈한다.
// 게임의 WrapText 는 글자를 하나 붙일 때마다 줄 전체의 너비를 다시 재서(글자 수 × 줄 길이 만큼 글자 그리기)
// iPhone 에서는 창이 뜨기 전에 오래 멈춘다. 그래서 패치된 ReleaseNotes.All() 이 시작할 때 이 메서드를 거쳐
// 새 판(맨 위)부터 일정 분량까지만 남긴다. (NotesHook 이 연결)
public static class NotesTrim
{
    const int MaxChars = 6000;     // 이만큼 넘기 전까지 판 단위로 담는다
    const int MaxSections = 40;
    const string Tail = "\n\n## v0.0.1\n(오래된 판의 내역은 줄여서 보여 드립니다. 전체는 GitHub 릴리즈 노트에서 볼 수 있습니다.)\n";

    public static string Head(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxChars) return text;

            string t = text.Replace("\r\n", "\n");
            var sb = new StringBuilder();
            int pos = 0, sections = 0;
            bool comment = false;
            int cut = -1;

            while (pos < t.Length)
            {
                int nl = t.IndexOf('\n', pos);
                int end = nl < 0 ? t.Length : nl;
                string line = t.Substring(pos, end - pos).TrimEnd();

                if (line.StartsWith("<!--")) comment = true;
                bool inComment = comment;
                if (comment && line.EndsWith("-->")) comment = false;

                if (!inComment && line.StartsWith("## "))
                {
                    if (sections >= MaxSections || (sections >= 1 && sb.Length >= MaxChars)) { cut = pos; break; }
                    sections++;
                }
                sb.Append(t, pos, (nl < 0 ? t.Length : nl + 1) - pos);
                if (nl < 0) break;
                pos = nl + 1;
            }

            return cut < 0 ? text : sb.ToString().TrimEnd() + Tail;
        }
        catch { return text; }
    }
}
