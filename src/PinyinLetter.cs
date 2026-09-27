// PinyinLetter.cs —— 把中文首字算成拼音首字母，让「全部」列表能按 A/B/C 分组
//
// 为什么这么绕：Windows 开始菜单的字母分组是系统内部算的，没有公开 API；
// 而 .NET Framework 自带库里没有拼音表。这里用系统自带的 zh-CN 排序规则（按拼音排）
// 和每个字母的「标杆字」做二分比较，属于纯本地只读运算，不联网、不装库。
// 实测：微信→W、计算器→J、设置→S、截图→J、图片→T、哔哩哔哩→B。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace BreadLauncher
{
    public static class PinyinLetter
    {
        // 每个拼音首字母挑一个代表字，顺序必须保持「按拼音递增」
        private static readonly string Letters = "ABCDEFGHJKLMNOPQRSTWXYZ";
        private static readonly string[] Anchors = new string[]
        {
            "阿", "八", "擦", "搭", "蛾", "发", "噶", "哈", "击", "喀", "垃", "妈", "那",
            "哦", "啪", "七", "然", "撒", "塌", "挖", "昔", "压", "匝"
        };

        private static readonly Dictionary<char, string> Cache = new Dictionary<char, string>();
        private static readonly object CacheLock = new object();

        private static readonly CultureInfo Chinese = ResolveCulture();

        private static CultureInfo ResolveCulture()
        {
            try { return CultureInfo.GetCultureInfo("zh-CN"); }
            catch { return CultureInfo.CurrentCulture; }
        }

        /// <summary>返回 "A".."Z"，算不出来返回 "#"（数字、符号、拉丁字母以外的文字）。</summary>
        public static string Of(char c)
        {
            if (c >= 'a' && c <= 'z') return ((char)(c - 32)).ToString(CultureInfo.InvariantCulture);
            if (c >= 'A' && c <= 'Z') return c.ToString(CultureInfo.InvariantCulture);

            lock (CacheLock)
            {
                string hit;
                if (Cache.TryGetValue(c, out hit)) return hit;
            }

            string result = Resolve(c);

            lock (CacheLock)
            {
                Cache[c] = result;
            }
            return result;
        }

        private static string Resolve(char c)
        {
            // 只处理汉字区，其它（数字、日文假名、韩文等）落到 #
            if (c < 0x4E00 || c > 0x9FFF) return "#";
            string s = c.ToString();
            string best = "#";
            for (int i = 0; i < Anchors.Length; i++)
            {
                int cmp;
                try { cmp = string.Compare(s, Anchors[i], true, Chinese); }
                catch { return "#"; }
                if (cmp >= 0) best = Letters[i].ToString(CultureInfo.InvariantCulture);
                else break;
            }
            return best;
        }
    }
}