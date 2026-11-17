using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution.Gal
{
    [Serializable]
    public sealed class RevGalVariableValue
    {
        public string Id;
        public int Value;
    }

    [Serializable]
    public sealed class RevGalHistoryEntry
    {
        public string Node;
        public string Choice;
    }

    /// <summary>存档保存节点、变量、舞台与本次回看记录。已读和收藏进度独立于存档槽。</summary>
    [Serializable]
    public sealed class RevGalSave
    {
        public int Schema = 1;
        public string Story;
        public int Revision;
        public string Node;
        public RevGalStage Stage;
        public List<RevGalVariableValue> Variables;
        public List<RevGalHistoryEntry> History;
    }

    [Serializable]
    public sealed class RevGalProgress
    {
        public int Schema = 1;
        public List<string> Read = new List<string>();
        public List<string> Endings = new List<string>();
        public List<string> Gallery = new List<string>();
    }

    /// <summary>宿主可以接入自己的存档、云端同步或测试内存实现。键按剧情和存档槽区分。</summary>
    public interface IRevGalStorage
    {
        RevGalSave ReadSlot(string story, string slot);
        void WriteSlot(string story, string slot, RevGalSave save);
        RevGalProgress ReadProgress(string story);
        void WriteProgress(string story, RevGalProgress progress);
    }

    /// <summary>小型本地存档默认实现。大型或云端项目可注入自己的存储实现。</summary>
    public sealed class RevGalLocalStorage : IRevGalStorage
    {
        public RevGalSave ReadSlot(string story, string slot)
        {
            return Read<RevGalSave>(Key(story, "slot." + CheckSlot(slot)));
        }

        public void WriteSlot(string story, string slot, RevGalSave save)
        {
            Write(Key(story, "slot." + CheckSlot(slot)), save);
        }

        public RevGalProgress ReadProgress(string story)
        {
            return Read<RevGalProgress>(Key(story, "progress"));
        }

        public void WriteProgress(string story, RevGalProgress progress)
        {
            Write(Key(story, "progress"), progress);
        }

        private static string CheckSlot(string slot)
        {
            return RevGalValidation.IsId(slot) ? slot : throw new ArgumentException("存档槽 ID 格式错误", nameof(slot));
        }

        private static string Key(string story, string suffix)
        {
            if (!RevGalValidation.IsId(story))
            {
                throw new ArgumentException("剧情 ID 格式错误", nameof(story));
            }
            return "Revolution.Gal.v1:" + story + ":" + suffix;
        }

        private static T Read<T>(string key) where T : class
        {
            string value = PlayerPrefs.GetString(key, "");
            return string.IsNullOrEmpty(value) ? null : JsonUtility.FromJson<T>(value);
        }

        private static void Write<T>(string key, T value)
        {
            PlayerPrefs.SetString(key, JsonUtility.ToJson(value));
            PlayerPrefs.Save();
        }
    }
}
