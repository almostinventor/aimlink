using System;
using UnityEngine;

namespace AimLink
{
    public enum AppState { Live, Blackout, Boot }

    /// <summary>LIVE --Y--> BLACKOUT --Y--> BOOT --(sequence ends)--> LIVE. Everything else subscribes to StateChanged.</summary>
    public sealed class AppStateMachine : MonoBehaviour
    {
        [Tooltip("State the app launches into.")]
        [SerializeField] AppState startState = AppState.Live;
        [Tooltip("Dev convenience: holding Y jumps straight to LIVE from any state.")]
        [SerializeField] bool holdYForLive = true;
        [SerializeField] InputRouter input;

        public AppState State { get; private set; }

        /// <summary>(previous, next). Also raised once in Start with previous == next for initial setup.</summary>
        public event Action<AppState, AppState> StateChanged;

        void Awake() { State = startState; }

        void OnEnable()
        {
            if (input == null) return;
            input.YPressed += OnYPressed;
            input.YHeld += OnYHeld;
        }

        void OnDisable()
        {
            if (input == null) return;
            input.YPressed -= OnYPressed;
            input.YHeld -= OnYHeld;
        }

        void Start()
        {
            if (input != null) input.TrimEnabled = State == AppState.Live;
            StateChanged?.Invoke(State, State);
        }

        void OnYPressed()
        {
            switch (State)
            {
                case AppState.Live: Set(AppState.Blackout); break;
                case AppState.Blackout: Set(AppState.Boot); break;
                // Y is ignored while booting
            }
        }

        void OnYHeld()
        {
            if (holdYForLive) Set(AppState.Live);
        }

        /// <summary>Called by BootSequence when it finishes.</summary>
        public void NotifyBootFinished()
        {
            if (State == AppState.Boot) Set(AppState.Live);
        }

        void Set(AppState next)
        {
            if (next == State) return;
            AppState prev = State;
            State = next;
            if (input != null) input.TrimEnabled = next == AppState.Live;
            StateChanged?.Invoke(prev, next);
        }
    }
}
