namespace RevGame.Pvp.Agent
{
    /// <summary>运行时配置。Key仅在内存使用；持久化由独立存储类加密后写入本地，不发给联机服务器。</summary>
    public sealed class PvpAgentSettings
    {
        public string Key
        {
            get;
            private set;
        } = "";
        public string Model
        {
            get;
            private set;
        } = "deepseek-flash";
        public bool Enabled
        {
            get;
            private set;
        }
        public bool Ready => Enabled && !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Model);

        public bool TrySet(string key, string model, bool enabled, out string reason)
        {
            key = (key ?? "").Trim();
            model = (model ?? "").Trim();
            reason = "";
            if (key.Length > 512 || model.Length > 128 || key.Contains("\n") || key.Contains("\r") || model.Contains("\n") || model.Contains("\r"))
            {
                reason = "Key或模型格式无效";
                return false;
            }
            if (enabled && (key.Length == 0 || model.Length == 0))
            {
                reason = "启用前请填写Key与模型";
                return false;
            }
            Key = key;
            Model = model;
            Enabled = enabled;
            return true;
        }
    }
}
