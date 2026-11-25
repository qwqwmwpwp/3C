using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色数据/05 移动预测", fileName = "05 移动预测")]
    public sealed class CharacterForecastConfig : ScriptableObject
    {
        [SerializeField, InspectorName("移动预测")] internal CharacterForecastSettings Forecast = new CharacterForecastSettings();
    }
}
