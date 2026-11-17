using System;
using UnityEngine;

/// <summary>项目模板介绍资产，由配套自定义 Inspector 显示图标、标题、正文和链接。</summary>
public class Readme : ScriptableObject
{
    [Tooltip("介绍页标题旁显示的图标。")]
    public Texture2D icon;
    [Tooltip("项目介绍页标题。")]
    public string title;
    [Tooltip("按顺序展示的介绍段落，可包含标题、正文和外部链接。")]
    public Section[] sections;
    [Tooltip("记录模板布局是否已加载，由介绍页编辑器管理。")]
    public bool loadedLayout;

    /// <summary>介绍页的一段内容，链接显示文字与目标地址分别保存。</summary>
    [Serializable]
    public class Section
    {
        public string heading, text, linkText, url;
    }
}
