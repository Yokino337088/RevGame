#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>开发态只读快照：比较真实Renderer、模型合法性与资源引用，不修改棋盘来制造通过。</summary>
        public object MagicSnapshot()
        {
            var materials = new HashSet<int>();
            var eyeStates = new List<object>();
            int highlights = 0, invalidHighlights = 0, missingHighlights = 0;
            int animatedCells = 0, renderers = 0;
            bool eyePacked = true, supported = true;
            var battle = RevServices.GetRequired<IBattleService>();
            var tutorial = RevLogic.Get<RevGame.Flow.TutorialLogic>();
            bool guided = tutorial != null && tutorial.Active;
            foreach (BoardCellView cell in _cells)
            {
                SpriteRenderer surface = cell.SurfaceRenderer;
                if (surface != null && surface.sharedMaterial != null)
                {
                    materials.Add(surface.sharedMaterial.GetInstanceID());
                    supported &= surface.sharedMaterial.shader.isSupported;
                    if (surface.sharedMaterial == _surfaceMaterial) { animatedCells++; }
                }
                renderers += cell.GetComponentsInChildren<SpriteRenderer>(true).Length;
                bool expected = !guided && (battle.CanPlaySelectedAt(cell.Position) || (battle.SelectedPieceId != 0 && battle.Board.GetPiece(cell.Position)?.Id == battle.SelectedPieceId));
                if (cell.IsHighlighted) { highlights++; if (!expected) { invalidHighlights++; } }
                else if (expected) { missingHighlights++; }
                if (cell.IsEyeVisible)
                {
                    SpriteRenderer eye = cell.transform.Find("眼").GetComponent<SpriteRenderer>();
                    eyePacked &= eye.sprite != null && eye.sprite.packed && cell.EyeFrameCount == 4;
                    eyeStates.Add(new Dictionary<string,object> { ["x"] = cell.Position.X, ["y"] = cell.Position.Y, ["frame"] = cell.EyeFrame, ["name"] = eye.sprite == null ? "" : eye.sprite.name });
                }
            }
            var refs = new List<int>();
            foreach (string name in SurfaceNames) { refs.Add(RevResManager.GetRefCount(SurfaceRoot, name + "_Surface")); }
            return new Dictionary<string,object>
            {
                ["chapter"] = SurfaceChapter, ["ready"] = _surfaceReady, ["cells"] = _cells.Count,
                ["shader"] = _surfaceMaterial == null ? "" : _surfaceMaterial.shader.name, ["supported"] = supported,
                ["materialCount"] = materials.Count, ["animatedCells"] = animatedCells, ["cellRenderers"] = renderers,
                ["highlights"] = highlights, ["invalidHighlights"] = invalidHighlights, ["missingHighlights"] = missingHighlights,
                ["placementShader"] = _cells.Count == 0 ? "" : _cells[0].PlacementRenderer.sharedMaterial.shader.name,
                ["eyes"] = eyeStates, ["eyePacked"] = eyePacked, ["materialRefs"] = refs,
                ["error"] = LoadError ?? ""
            };
        }
    }
}
#endif
