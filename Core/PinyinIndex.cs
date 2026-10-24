using System.Text;

namespace MacDock.Core;

/// <summary>
/// 汉字 → 拼音首字母：启动台的搜索要能用「wx」找到「微信」、「wyyy」找到「网易云音乐」。
///
/// 表不在源码里逐字写死，而是首次用到时由 Windows 自带的 GB2312 代码页现算：GB2312 一级字库
/// （16~55 区，3755 个最常用字）本身就是按拼音排序的，只要知道每个字母那一组的首字，就能给整组
/// 打上标记。二级字库按部首排，认不出拼音，这些字干脆不入表——搜索回落到名称匹配，宁可不命中，
/// 也不给出错的拼音。表算一次约几毫秒，放在启动台后台扫描线程里做，UI 线程不碰。
/// </summary>
internal static class PinyinIndex
{
    /// <summary>GB2312 一级字库每一组拼音的首字（啊芭擦搭蛾发噶哈击喀垃妈拿哦啪期然撒塌挖昔压匝）。拼音没有 i / u / v。</summary>
    const string GroupOpeners = "啊芭擦搭蛾发噶哈击喀垃妈拿哦啪期然撒塌挖昔压匝";
    const string GroupLetters = "abcdefghjklmnopqrstwxyz";

    const int Gb2312 = 936;

    /// <summary>
    /// 多音字按软件名里更常见的那个读音纠正。GB2312 只能给一个字一个字母，纠正表补上少数撞车的字；
    /// 「乐」是必须的：它在字库里按 lè 排，而「音乐」读 yuè。
    /// </summary>
    static readonly Dictionary<char, char> Overrides = new()
    {
        ['乐'] = 'y', // 音乐 yuè；否则「网易云音乐」算成 wyyyl
        ['行'] = 'x', // 银行、行业
        ['长'] = 'c', // 长城、成长
        ['重'] = 'z', // 重庆 chóng
        ['数'] = 's', // 数据
        ['会'] = 'h',
        ['觉'] = 'j',
        ['只'] = 'z',
        ['说'] = 's',
        ['更'] = 'g',
        ['都'] = 'd',
        ['还'] = 'h',
        ['传'] = 'c',
        ['系'] = 'x', // 系统
        ['地'] = 'd',
        ['分'] = 'f',
    };

    static readonly Dictionary<char, char> Letters = Build();

    /// <summary>一个字的拼音首字母；表里没有的字返回 <c>'\0'</c>（生僻字、二级字库、非汉字）。</summary>
    public static char Initial(char c) =>
        Overrides.TryGetValue(c, out var overridden) ? overridden
        : Letters.TryGetValue(c, out var letter) ? letter
        : '\0';

    /// <summary>
    /// 一个名字的首字母串：汉字取拼音首字母，拉丁词取每个词的首字母。
    /// 「微信」→ wx，「网易云音乐」→ wyyy，「Visual Studio Code」→ vsc。
    /// </summary>
    public static string Initials(string name)
    {
        var text = new StringBuilder(name.Length);
        bool wordStart = true;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (c < 0x80)
                {
                    if (wordStart) text.Append(char.ToLowerInvariant(c));
                }
                else if (Initial(c) is var letter and not '\0')
                {
                    text.Append(letter);
                }
                wordStart = false;
            }
            else
            {
                wordStart = true; // 空格、标点、符号：下一个字母算新词的词首
            }
        }
        return text.ToString();
    }

    /// <summary>把 GB2312 一级字库扫一遍，逐字打上所在拼音组的字母。</summary>
    static Dictionary<char, char> Build()
    {
        var table = new Dictionary<char, char>(4096);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var gb = Encoding.GetEncoding(Gb2312);

            // 一级字库：高位 0xB0~0xD7，低位 0xA1~0xFE，按拼音排列。
            var chars = new List<char>(3800);
            for (int high = 0xB0; high <= 0xD7; high++)
            {
                for (int low = 0xA1; low <= 0xFE; low++)
                {
                    var text = gb.GetString([(byte)high, (byte)low]);
                    if (text.Length == 1 && text[0] is >= '\u4E00' and <= '\u9FFF') chars.Add(text[0]);
                }
            }

            // 每组首字在字库里的位置就是这一组的起点。
            var starts = new List<(int At, char Letter)>();
            for (int i = 0; i < GroupOpeners.Length && i < GroupLetters.Length; i++)
            {
                int at = chars.IndexOf(GroupOpeners[i]);
                if (at < 0) Log.Info($"Pinyin: 首字「{GroupOpeners[i]}」不在 GB2312 一级字库里，该组只用纠正表");
                else starts.Add((at, GroupLetters[i]));
            }
            if (starts.Count == 0)
            {
                Log.Info("Pinyin: 一个字都没认出来，搜索只按名称匹配");
                return table;
            }
            starts.Sort((a, b) => a.At.CompareTo(b.At));

            int group = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                while (group + 1 < starts.Count && i >= starts[group + 1].At) group++;
                if (i >= starts[group].At) table[chars[i]] = starts[group].Letter;
            }
            Log.Info($"Pinyin: {table.Count} 个常用字进了首字母表");
        }
        catch (Exception ex)
        {
            // 没有代码页支持（精简的运行环境）：表为空，搜索退回名称匹配，启动台照常能用。
            Log.Error(ex, "Building the pinyin table");
        }
        return table;
    }
}
