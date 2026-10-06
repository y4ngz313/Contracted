using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    internal sealed class WhistleblowerFallbackVisual : MonoBehaviour
    {
        private Transform _body;
        private Transform _head;
        private Transform _leftUpperArm;
        private Transform _leftForearm;
        private Transform _rightUpperArm;
        private Transform _rightForearm;
        private Transform _leftLeg;
        private Transform _rightLeg;
        private float _phaseOffset;

        internal void Build(Material suit, Material trim, Material skin)
        {
            if (_body != null)
                return;

            _phaseOffset = Random.Range(0f, 10f);

            _body = CreatePart("Body", PrimitiveType.Capsule, suit, new Vector3(0f, 1.22f, 0f), new Vector3(0.46f, 0.62f, 0.32f)).transform;
            _head = CreatePart("Head", PrimitiveType.Sphere, skin, new Vector3(0f, 2.22f, 0f), new Vector3(0.33f, 0.36f, 0.33f)).transform;
            CreatePart("Neck", PrimitiveType.Cube, skin, new Vector3(0f, 1.93f, 0f), new Vector3(0.18f, 0.18f, 0.16f));
            CreatePart("LeftBoot", PrimitiveType.Cube, trim, new Vector3(-0.16f, 0.12f, 0f), new Vector3(0.17f, 0.24f, 0.24f));
            CreatePart("RightBoot", PrimitiveType.Cube, trim, new Vector3(0.16f, 0.12f, 0f), new Vector3(0.17f, 0.24f, 0.24f));

            _leftLeg = CreatePart("LeftLeg", PrimitiveType.Capsule, suit, new Vector3(-0.16f, 0.55f, 0f), new Vector3(0.18f, 0.42f, 0.18f)).transform;
            _rightLeg = CreatePart("RightLeg", PrimitiveType.Capsule, suit, new Vector3(0.16f, 0.55f, 0f), new Vector3(0.18f, 0.42f, 0.18f)).transform;

            _leftUpperArm = CreatePart("LeftUpperArm", PrimitiveType.Cube, suit, new Vector3(-0.52f, 1.64f, 0f), new Vector3(0.13f, 0.56f, 0.13f)).transform;
            _leftForearm = CreatePart("LeftForearm", PrimitiveType.Cube, suit, new Vector3(-0.78f, 2.03f, 0f), new Vector3(0.12f, 0.48f, 0.12f)).transform;
            _rightUpperArm = CreatePart("RightUpperArm", PrimitiveType.Cube, suit, new Vector3(0.52f, 1.64f, 0f), new Vector3(0.13f, 0.56f, 0.13f)).transform;
            _rightForearm = CreatePart("RightForearm", PrimitiveType.Cube, suit, new Vector3(0.78f, 2.03f, 0f), new Vector3(0.12f, 0.48f, 0.12f)).transform;

            ApplyPose(scared: true, dead: false, speed: 0f);
        }

        internal void ApplyPose(bool scared, bool dead, float speed)
        {
            if (_body == null)
                return;

            float walk = Mathf.Clamp01(speed);
            float t = Time.time + _phaseOffset;
            float jitter = scared ? Mathf.Sin(t * 13f) * 4f : 0f;
            float sway = Mathf.Sin(t * 6f) * walk * 5f;

            transform.localRotation = dead
                ? Quaternion.Euler(86f, 0f, 0f)
                : Quaternion.Euler(0f, sway * 0.2f, 0f);
            transform.localPosition = dead
                ? new Vector3(0f, 0.08f, -0.25f)
                : new Vector3(0f, Mathf.Sin(t * 4f) * walk * 0.025f, 0f);

            _head.localRotation = Quaternion.Euler(scared ? 5f + jitter : 0f, scared ? jitter * 0.5f : 0f, 0f);
            _leftUpperArm.localRotation = Quaternion.Euler(0f, 0f, -64f - sway - jitter);
            _leftForearm.localRotation = Quaternion.Euler(0f, 0f, -18f - jitter * 0.6f);
            _rightUpperArm.localRotation = Quaternion.Euler(0f, 0f, 64f + sway + jitter);
            _rightForearm.localRotation = Quaternion.Euler(0f, 0f, 18f + jitter * 0.6f);
            _leftLeg.localRotation = Quaternion.Euler(Mathf.Sin(t * 7f) * walk * 5f, 0f, 0f);
            _rightLeg.localRotation = Quaternion.Euler(-Mathf.Sin(t * 7f) * walk * 5f, 0f, 0f);
        }

        private GameObject CreatePart(string partName, PrimitiveType primitive, Material material, Vector3 localPosition, Vector3 localScale)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = partName;
            part.transform.SetParent(transform, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            part.layer = 0;

            var collider = part.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var renderer = part.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.allowOcclusionWhenDynamic = false;
                renderer.forceRenderingOff = false;
            }

            return part;
        }
    }
}
