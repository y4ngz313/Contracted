using System.Collections.Generic;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteShipController
    {
        private sealed class HoverMesh { internal MeshRenderer Source, Copy; internal bool Enabled; }
        private readonly List<HoverMesh> _hoverMeshes = new List<HoverMesh>();
        private Transform _hoverVisualRoot;
        private float _hoverBlend;
        private void ApplyHoverVisual()
        {
            bool hover = IsHovering && IsGrounded;
            if (!hover && _hoverVisualRoot == null) return;
            if (_hoverVisualRoot == null)
            {
                // Keep colliders, navigation, actors and magnet sockets on the stationary ship.
                var sources = GetComponentsInChildren<MeshRenderer>(true);
                _hoverVisualRoot = new GameObject("HoverPresentation").transform; _hoverVisualRoot.SetParent(transform, false);
                foreach (var source in sources)
                {
                    var filter = source.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null || source.name.Contains("Flame") || source.GetComponentInParent<BlacksiteRopeVisual>() != null) continue;
                    var copy = new GameObject(source.name); copy.layer = source.gameObject.layer; copy.transform.SetParent(_hoverVisualRoot, false);
                    copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    var renderer = copy.AddComponent<MeshRenderer>(); renderer.sharedMaterials = source.sharedMaterials;
                    renderer.shadowCastingMode = source.shadowCastingMode; renderer.receiveShadows = source.receiveShadows;
                    _hoverMeshes.Add(new HoverMesh { Source = source, Copy = renderer, Enabled = source.enabled });
                }
            }
            _hoverBlend = Mathf.MoveTowards(_hoverBlend, hover ? 1 : 0, Time.deltaTime * 2);
            float time = (float)CargoNow;
            var turn = Quaternion.Euler(Mathf.Sin(time*.73f)*.3f*_hoverBlend, 0, Mathf.Sin(time*.91f)*.45f*_hoverBlend);
            var shift = Vector3.up * (Mathf.Sin(time*1.05f)*.045f*_hoverBlend);
            foreach (var mesh in _hoverMeshes)
            {
                if (mesh.Source == null || mesh.Copy == null) continue;
                mesh.Source.enabled = _hoverBlend == 0 && mesh.Enabled;
                mesh.Copy.enabled = _hoverBlend > 0 && mesh.Enabled && mesh.Source.gameObject.activeInHierarchy;
                var local = transform.InverseTransformPoint(mesh.Source.transform.position);
                mesh.Copy.transform.SetPositionAndRotation(transform.TransformPoint(turn * local + shift), transform.rotation * turn * Quaternion.Inverse(transform.rotation) * mesh.Source.transform.rotation);
                var scale = mesh.Source.transform.lossyScale; var parentScale = _hoverVisualRoot.lossyScale;
                mesh.Copy.transform.localScale = new Vector3(scale.x/parentScale.x,scale.y/parentScale.y,scale.z/parentScale.z);
            }
        }
    }
}
