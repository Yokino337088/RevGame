// ============================================================
// LevelSelectPanel.cs —— 六章选关。
// 每个按钮固定提交章号；是否解锁由服务判定，禁用外观只是提示，不能替代规则校验。
// ============================================================
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEngine.Events;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI, RevUILayer.Normal)]
    public sealed class LevelSelectPanel : RevUIPanel
    {
        [RevBind]
        private Button _btnHome;
        [RevBind]
        private Text _hint;
        private Button[] _chapters;
        private UnityAction[] _actions;
        protected override void OnBindView()
        {
            _chapters = new Button[Core.Consts.ChapterCount];
            _actions = new UnityAction[_chapters.Length];
            for (int i = 0; i < _chapters.Length; i++)
            {
                int chapter = i + 1;
                _chapters[i] = transform.Find("chapter" + chapter).GetComponent<Button>();
                _actions[i] = () => OnChapter(chapter);
                _chapters[i].onClick.AddListener(_actions[i]);
            }

            _btnHome.onClick.AddListener(OnHome);
        }

        protected override void OnRefreshView()
        {
            int unlocked = RevServices.GetRequired<IBattleService>().UnlockedChapter;
            for (int i = 0; i < _chapters.Length; i++)
            {
                _chapters[i].interactable = i < unlocked;
                _chapters[i].GetComponentInChildren<Text>().text = "第 " + (i + 1) + " 章\n" + (i < unlocked ? "开始挑战" : "通关前章解锁");
            }

            _hint.text = "每章 10 个普通回合 + 1 个 Boss 回合\n同章保留残局，失败重开本章";
        }

        private void OnChapter(int chapter)
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (!flow.EnterBattle(chapter))
            {
                _hint.text = flow.LastError;
            }
        }

        private void OnHome()
        {
            RevServices.GetRequired<GameFlow>().ReturnHome();
        }

        protected override void OnRelease()
        {
            for (int i = 0; i < _chapters.Length; i++)
            {
                _chapters[i].onClick.RemoveListener(_actions[i]);
            }

            _btnHome.onClick.RemoveListener(OnHome);
        }
    }
}
