using UnityEngine;

namespace RevGame.Data
{
    /// <summary>项目书随界面一起加载。作者可以在 Inspector 编写第二页正文和更新群邀请。</summary>
    [CreateAssetMenu(menuName = "RevGame/项目开源/项目书配置", fileName = "ProjectOpenSourceConfig")]
    public sealed class ProjectOpenSourceConfig : ScriptableObject
    {
        [SerializeField, InspectorName("作者的话"), TextArea(8, 20)]
        private string authorMessage = "";

        [SerializeField, InspectorName("QQ群邀请链接"), Tooltip("可填写 QQ 生成的 HTTPS 加群链接。留空时按群号打开 QQ 群名片。")]
        private string groupInviteUrl = "";

        public string AuthorMessage => authorMessage;
        public string GroupInviteUrl => groupInviteUrl;
    }
}
