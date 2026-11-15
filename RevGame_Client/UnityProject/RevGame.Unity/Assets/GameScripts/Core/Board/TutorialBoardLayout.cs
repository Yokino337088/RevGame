namespace RevGame.Core
{
    /// <summary>教学开局与输入提示共用固定坐标，战斗服务无需依赖场景流程。</summary>
    public static class TutorialBoardLayout
    {
        public static readonly BoardPos EnemyPosition = new BoardPos(4, 4);
        public static readonly BoardPos SealPosition = new BoardPos(4, 3);
        public static readonly BoardPos AllyPosition = new BoardPos(3, 4);
        public static readonly BoardPos MovePosition = new BoardPos(2, 4);
    }
}
