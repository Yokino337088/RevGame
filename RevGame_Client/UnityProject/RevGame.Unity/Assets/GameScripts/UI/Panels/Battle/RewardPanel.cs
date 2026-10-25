// ============================================================
// RewardPanel.cs —— 章节结束的三选一。
// 数据由服务在打开面板前确定；这里只读标题、说明和图标，点击只提交选项编号。
// ============================================================
using System.Collections.Generic;
using RevGame.Data;
using RevGame.Flow;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class RewardPanel : RevUIPanel<RewardPanel.RewardData>
    {
        public sealed class RewardData
        {
            public IReadOnlyList<RewardDef> Options;
        }

        [RevBind]
        private Button _choice0;
        [RevBind]
        private Button _choice1;
        [RevBind]
        private Button _choice2;
        [SerializeField]
        private Sprite[] rewardIcons;
        protected override void OnBindView()
        {

        }

        protected override void OnRefreshView()
        {
            if (Data == null || Data.Options == null)
            {
                return;
            }

            Button[] buttons =
            {
                _choice0,
                _choice1,
                _choice2
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                RewardDef reward = Data.Options[i];
                buttons[i].transform.Find("label").GetComponent<Text>().text = reward.Title + "\n\n" + reward.Description;
                buttons[i].transform.Find("icon").GetComponent<Image>().sprite = rewardIcons[(int)reward.Kind];
            }
        }

        [RevButtonClick("choice0")]
        private void OnFirst()
        {
            RevLogic.Get<BattleLogic>().ChooseReward(0);
        }

        [RevButtonClick("choice1")]
        private void OnSecond()
        {
            RevLogic.Get<BattleLogic>().ChooseReward(1);
        }

        [RevButtonClick("choice2")]
        private void OnThird()
        {
            RevLogic.Get<BattleLogic>().ChooseReward(2);
        }


    }
}
