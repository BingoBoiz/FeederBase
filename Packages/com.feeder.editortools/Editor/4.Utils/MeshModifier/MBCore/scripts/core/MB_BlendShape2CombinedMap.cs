using UnityEngine;

namespace Feeder.MB.Core
{
    [UnityEngine.AddComponentMenu("")]
    public class MB_BlendShape2CombinedMap : MonoBehaviour
    {
        public SerializableSourceBlendShape2Combined srcToCombinedMap;

        public SerializableSourceBlendShape2Combined GetMap()
        {
            if (srcToCombinedMap == null)
            {
                srcToCombinedMap = new SerializableSourceBlendShape2Combined();
            }

            return srcToCombinedMap;
        }
    }
}
