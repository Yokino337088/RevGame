using System;
using System.Globalization;
using Revolution;
using UnityEngine;

namespace RevGame.Services
{
    /// <summary>
    /// 启动宿主持有的本地积分存档；只保存总量，不保存棋盘。PlayerPrefs适用于当前离线小游戏。
    /// 击中时只标脏，至多每2秒落盘一次，结算/离开战斗/切后台立即刷盘；异常退出最多丢失最近2秒。
    /// 2秒是磁盘写入节流策略，不是玩法数值；不在每次高频暴击中调用Save造成主线程卡顿。
    /// </summary>
    public sealed class ScoreProgressStore : IScoreProgress, IDisposable
    {
        private readonly string _key;
        private bool _dirty;
        public long Total
        {
            get; private set;
        }
        public ScoreProgressStore() : this("RevGame.Score.Total.v1") { }

        /// <summary>显式存储键供隔离持久化验收使用，正式启动使用无参构造；绝不在测试中备份覆盖玩家键。</summary>
        public ScoreProgressStore(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("积分存档键不能为空", nameof(key));
            }
            _key = key;
            string saved = PlayerPrefs.GetString(_key, "0");

            if (!long.TryParse(saved, NumberStyles.None, CultureInfo.InvariantCulture, out long total) || total < 0)
            {
                RevLog.Warn("永久积分存档无效，按0显示；下一次真实得分后重建。", "RevGame");
                total = 0;
            }
            Total = total;
            RevTimer.Every(2f, Flush, owner: this);
            RevEvent.AddEventListener(RevAppLifecycle.Paused, Flush, owner: this);
            RevEvent.AddEventListener(RevAppLifecycle.Quitting, Flush, owner: this);
        }
        /// <summary>只接受递增总量，避免旧动画或迟到回调覆盖新积分；真正计分只在BattleService。</summary>
        public void Record(long total)
        {
            if (total <= Total)
            {
                return;
            }
            Total = total;
            _dirty = true;
        }
        public void Flush()
        {
            if (!_dirty)
            {
                return;
            }
            PlayerPrefs.SetString(_key, Total.ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
            _dirty = false;
        }
        public void Dispose()
        {
            Flush();
            RevTimer.CancelAllOf(this);
            RevEvent.RemoveAllByOwner(this);
        }
    }
}
