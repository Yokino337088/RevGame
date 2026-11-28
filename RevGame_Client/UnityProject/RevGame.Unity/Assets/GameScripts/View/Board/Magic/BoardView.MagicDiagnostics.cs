#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>开发态读取实际格面材质、第四章光斑开关、棋盘提示和资源引用。</summary>
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

                    if (surface.sharedMaterial == _surfaceMaterial)
                    {
                        animatedCells++;
                    }
                }

                renderers += cell.GetComponentsInChildren<SpriteRenderer>(true).Length;
                bool expected = !guided && (battle.CanPlaySelectedAt(cell.Position) || (battle.SelectedPieceId != 0 && battle.Board.GetPiece(cell.Position)?.Id == battle.SelectedPieceId));

                if (cell.IsHighlighted)
                {
                    highlights++;

                    if (!expected)
                    {
                        invalidHighlights++;
                    }
                }

                else
                    if (expected)
                    {
                        missingHighlights++;
                    }
                if (cell.IsEyeVisible)
                {
                    SpriteRenderer eye = cell.transform.Find("眼").GetComponent<SpriteRenderer>();
                    eyePacked &= eye.sprite != null && eye.sprite.packed && cell.EyeFrameCount == 4;
                    eyeStates.Add(new Dictionary<string, object> { ["x"] = cell.Position.X, ["y"] = cell.Position.Y, ["frame"] = cell.EyeFrame, ["name"] = eye.sprite == null ? "" : eye.sprite.name });
                }
            }
            var refs = new List<int>();
            foreach (string name in SurfaceNames)
            {
                refs.Add(RevResManager.GetRefCount(SurfaceRoot, name + "_Surface"));
            }
            return new Dictionary<string, object>
            {
                ["chapter"] = SurfaceChapter,
                ["ready"] = _surfaceReady,
                ["cells"] = _cells.Count,
                ["shader"] = _surfaceMaterial == null ? "" : _surfaceMaterial.shader.name,
                ["supported"] = supported,
                ["materialCount"] = materials.Count,
                // 只读实际使用的第四章参数，供旧包与关闭环境光斑后的同场对照。
                ["swampAmbientGlow"] = SurfaceChapter == 4 && _surfaceMaterial != null ? (object)_surfaceMaterial.GetFloat(SwampMudHighlightStrength) : null,
                ["swampVisibleRim"] = SurfaceChapter == 4 && _surfaceMaterial != null
                    ? (object)(_surfaceMaterial.GetFloat("_RimStrength") * _surfaceMaterial.GetFloat("_Strength")) : null,
                ["swampEddyDensity"] = SurfaceChapter == 4 && _surfaceMaterial != null ? (object)_surfaceMaterial.GetFloat(SwampEddyDensity) : null,
                ["swampBubbleDensity"] = SurfaceChapter == 4 && _surfaceMaterial != null ? (object)_surfaceMaterial.GetFloat(SwampBubbleDensity) : null,
                ["animatedCells"] = animatedCells,
                ["cellRenderers"] = renderers,
                ["highlights"] = highlights,
                ["invalidHighlights"] = invalidHighlights,
                ["missingHighlights"] = missingHighlights,
                ["placementShader"] = _cells.Count == 0 ? "" : _cells[0].PlacementRenderer.sharedMaterial.shader.name,
                ["eyeAnimationCells"] = _visibleEyeCells.Count,
                ["eyes"] = eyeStates,
                ["eyePacked"] = eyePacked,
                ["materialRefs"] = refs,
                ["error"] = LoadError ?? ""
            };
        }
    }
}
#endif
