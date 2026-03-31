using System.Collections.Generic;

namespace TheBookOfLong;

internal sealed partial class MelonPreferencesEditor
{
    private readonly Dictionary<int, bool> _blockedEventSystems = new();

    private void RefreshEventSystemBlocking(bool force)
    {
        float now = global::UnityEngine.Time.unscaledTime;
        if (!force && now < _nextEventSystemRefreshTime)
        {
            return;
        }

        _nextEventSystemRefreshTime = now + 1f;

        if (!_isVisible)
        {
            RestoreBlockedEventSystems();
            return;
        }

        global::UnityEngine.EventSystems.EventSystem[] eventSystems =
            global::UnityEngine.Object.FindObjectsOfType<global::UnityEngine.EventSystems.EventSystem>();

        for (int i = 0; i < eventSystems.Length; i += 1)
        {
            global::UnityEngine.EventSystems.EventSystem eventSystem = eventSystems[i];
            if (!eventSystem)
            {
                continue;
            }

            int instanceId = eventSystem.GetInstanceID();
            if (_blockedEventSystems.ContainsKey(instanceId))
            {
                continue;
            }

            _blockedEventSystems[instanceId] = eventSystem.enabled;
            if (eventSystem.enabled)
            {
                eventSystem.enabled = false;
            }
        }
    }

    private void RestoreBlockedEventSystems()
    {
        if (_blockedEventSystems.Count == 0)
        {
            return;
        }

        global::UnityEngine.EventSystems.EventSystem[] eventSystems =
            global::UnityEngine.Object.FindObjectsOfType<global::UnityEngine.EventSystems.EventSystem>();

        for (int i = 0; i < eventSystems.Length; i += 1)
        {
            global::UnityEngine.EventSystems.EventSystem eventSystem = eventSystems[i];
            if (!eventSystem)
            {
                continue;
            }

            int instanceId = eventSystem.GetInstanceID();
            if (_blockedEventSystems.TryGetValue(instanceId, out bool wasEnabled))
            {
                eventSystem.enabled = wasEnabled;
            }
        }

        _blockedEventSystems.Clear();
    }
}
