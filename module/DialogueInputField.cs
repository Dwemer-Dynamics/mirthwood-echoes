using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;

namespace Mirthwood.Echoes
{
    internal sealed class DialogueInputField : TMP_InputField
    {
        public override void OnSubmit(BaseEventData eventData)
        {
            // TMP handles Return and keypad Enter while editing. Mirthwood also binds
            // printable keyboard keys to menu Submit, which would send a partial draft.
            var submit = (eventData.currentInputModule as InputSystemUIInputModule)?.submit?.action;
            if (submit != null)
            {
                foreach (var control in submit.controls)
                {
                    if (!(control.device is Keyboard) && control is ButtonControl button && button.wasPressedThisFrame)
                    {
                        base.OnSubmit(eventData);
                        return;
                    }
                }
            }
            eventData.Use();
        }
    }
}
