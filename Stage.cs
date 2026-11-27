namespace OneClickFilm
{
    internal sealed class Stage
    {
        internal readonly string Name;
        internal readonly string Action;
        internal readonly string[] Outputs;

        private Stage(string name, string action, params string[] outputs)
        {
            Name = name;
            Action = action;
            Outputs = outputs;
        }

        internal static readonly Stage[] All =
        {
            new Stage("剧本导入与分析", "分析剧本", "剧本分析", "待制作资产清单"),
            new Stage("资产制作与自检", "制作资产图", "人物", "场景", "道具", "资产自检结果"),
            new Stage("分镜", "制作分镜", "镜头设计", "资产引用"),
            new Stage("分段", "划分片段", "镜头范围与预计时长"),
            new Stage("视频提示词", "编写提示词", "逐段提示词", "参考素材清单"),
            new Stage("视频生成", "生成视频", "片段视频", "提示词与分镜对应关系"),
            new Stage("成片交付", "制作成片", "完整视频")
        };
    }
}
