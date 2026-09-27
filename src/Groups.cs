// Groups.cs —— 「大文件夹」分组的数据结构
//
// 为什么单独一个文件：分组既要落盘（Settings.Groups），又要给自绘控件用，
// 中间还隔着一层「把 Key 解析成 AppEntry」的视图模型，塞进 Model.cs 会越滚越大。

using System;
using System.Collections.Generic;

namespace BreadLauncher
{
    /// <summary>一个分组（大文件夹）。只有组名和 AppEntry.Key 列表会落盘。</summary>
    public class AppGroup
    {
        public string Name;
        public List<string> Keys;

        public AppGroup()
        {
            Name = string.Empty;
            Keys = new List<string>();
        }

        public AppGroup(string name)
        {
            Name = name == null ? string.Empty : name;
            Keys = new List<string>();
        }

        public bool Contains(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < Keys.Count; i++)
                if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>加到尾；空 key 或已存在返回 false。</summary>
        public bool Add(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            key = key.Trim();
            if (key.Length == 0 || Contains(key)) return false;
            Keys.Add(key);
            return true;
        }

        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < Keys.Count; i++)
            {
                if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    Keys.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public override string ToString()
        {
            return Name + "(" + Keys.Count + ")";
        }
    }

    /// <summary>UI 用的视图模型：AppGroup 里的 Key 已经被解析成 AppEntry。</summary>
    public class GroupView
    {
        public AppGroup Group;
        public List<AppEntry> Apps = new List<AppEntry>();

        public string Name { get { return Group == null ? string.Empty : Group.Name; } }
    }
}