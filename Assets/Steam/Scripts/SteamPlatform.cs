using System;
using System.Text;
using Steamworks;
using UnityEngine;

namespace LockdownProtocol.Platform
{
    /// <summary>
    /// Steam 연동 진입점 (스팀용 저장소 전용 — Assets/Scripts/Platform/Steam 폴더에 둘 것).
    /// - 게임이 켜질 때 자동으로 만들어지고(씬에 배치 불필요) 씬이 바뀌어도 유지된다.
    /// - Steam 이 꺼져 있거나 초기화에 실패해도 오류 없이 넘어가고, 이때 모든 기능은 조용히 아무 일도 하지 않는다.
    /// - 닉네임: Steam 이름을 기본 닉네임으로 쓴다 (TryGetNickname).
    /// - 친구 초대: 방에 있는 동안 Steam 리치 프레전스 "connect" 에 방 코드를 올려, 친구 목록의 [게임 참가]로 들어오게 한다.
    ///   친구가 [게임 참가]를 누르면 JoinRequested 이벤트가 오고, 게임이 꺼져 있었다면 실행 인자(+connect 코드)로 받는다.
    /// </summary>
    [DisallowMultipleComponent]
    public class SteamPlatform : MonoBehaviour
    {
        /// <summary>Steam 이름을 게임 닉네임으로 쓸 때의 최대 글자 수 (LobbyPlayerController 의 한도와 같게 유지).</summary>
        public const int NicknameMaxLength = 12;

        private const float CallbackJoinExpireSeconds = 120f; //이미 방에 있을 때 받은 초대가 나중에 뜬금없이 실행되는 것 방지

        /// <summary>Steam 이 실제로 연결된 상태인지.</summary>
        public static bool Initialized { get; private set; }

        /// <summary>친구가 [게임 참가]를 눌렀을 때 (방 코드). 받는 쪽은 TryConsumePendingJoin 으로 코드를 가져가 쓴다.</summary>
        public static event Action<string> JoinRequested;

        private static SteamPlatform instance;
        private static string pendingJoinCode;
        private static float pendingExpireTime;

        private Callback<GameRichPresenceJoinRequested_t> joinRequestedCallback;
        private SteamAPIWarningMessageHook_t warningHook;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void resetStatics() //에디터 Play 재시작 때 남은 값 초기화
        {
            instance = null;
            Initialized = false;
            JoinRequested = null;
            pendingJoinCode = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void create() //첫 씬이 열리기 전에 한 번 만든다
        {
            if (instance != null) return;
            new GameObject("SteamPlatform").AddComponent<SteamPlatform>();
        }

        [AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
        private static void onSteamWarning(int severity, StringBuilder text)
        {
            Debug.LogWarning("[Steam] " + text);
        }

        private void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            try
            {
                if (!Packsize.Test()) Debug.LogError("[Steam] Packsize 가 맞지 않습니다. Steamworks.NET 플랫폼 설정을 확인하세요.");
                if (!DllCheck.Test()) Debug.LogError("[Steam] steam_api dll 과 Steamworks.NET 버전이 맞지 않습니다.");
                Initialized = SteamAPI.Init();
            }
            catch (DllNotFoundException exception)
            {
                Debug.LogWarning("[Steam] steam_api dll 을 찾지 못해 Steam 기능 없이 실행합니다.\n" + exception.Message);
                Initialized = false;
                return;
            }

            if (!Initialized)
            {
                Debug.LogWarning("[Steam] 초기화 실패 - Steam 기능 없이 실행합니다. (Steam 클라이언트 실행/로그인, 프로젝트 루트의 steam_appid.txt 를 확인하세요)");
                return;
            }

            warningHook = onSteamWarning;
            SteamClient.SetWarningMessageHook(warningHook);
            joinRequestedCallback = Callback<GameRichPresenceJoinRequested_t>.Create(onJoinRequested);
            readCommandLine();
            syncNicknameToPrefs();
            Debug.Log($"[Steam] 초기화 성공: {SteamFriends.GetPersonaName()}");
        }

        private void Update()
        {
            if (Initialized) SteamAPI.RunCallbacks();
        }

        private void OnDestroy()
        {
            if (instance != this) return;
            instance = null;
            if (!Initialized) return;
            joinRequestedCallback?.Dispose();
            joinRequestedCallback = null;
            SteamAPI.Shutdown();
            Initialized = false;
        }

        // ================== 닉네임 ==================

        /// <summary>Steam 이름을 게임 닉네임 규칙(태그 문자 제거, 최대 글자 수)에 맞춰 가져온다. Steam 미연결이면 false.</summary>
        public static bool TryGetNickname(int maxLength, out string nickname)
        {
            nickname = null;
            if (!Initialized) return false;

            string raw = SteamFriends.GetPersonaName();
            string cleaned = (raw ?? string.Empty).Replace("<", string.Empty).Replace(">", string.Empty).Trim();
            if (cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength);
                if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[cleaned.Length - 1]))
                    cleaned = cleaned.Substring(0, cleaned.Length - 1); //이모지 같은 두 칸 글자가 반으로 잘리는 것 방지
                cleaned = cleaned.Trim();
            }

            nickname = cleaned;
            return cleaned.Length > 0;
        }

        /// <summary>
        /// Steam 으로 실행 중이면 Steam 이름이 곧 닉네임이다. 예전에 직접 입력해 저장해 둔 닉네임이 있어도 덮어써서,
        /// 게임 화면(로비·인게임·초대 시스템)이 항상 Steam 이름과 같게 한다.
        /// </summary>
        private static void syncNicknameToPrefs()
        {
            if (!TryGetNickname(NicknameMaxLength, out string nickname)) return;
            PlayerPrefs.SetString("Nickname", nickname); //NicknamePopupUI.PrefsKey 와 같은 키 (PlayerPresence 도 이 값을 읽음)
            PlayerPrefs.Save();
        }

        // ================== 친구 초대 (리치 프레전스) ==================

        /// <summary>
        /// 방에 있는 동안 Steam 친구 목록에 상태와 [게임 참가] 코드를 표시한다.
        /// joinable 이 false 면 코드를 내려서(게임 중 등) 친구가 참가 버튼을 누르지 못하게 한다.
        /// </summary>
        public static void SetRoomPresence(string inviteCode, bool joinable, string status)
        {
            if (!Initialized) return;
            SteamFriends.SetRichPresence("connect", joinable && !string.IsNullOrEmpty(inviteCode) ? inviteCode : string.Empty);
            SteamFriends.SetRichPresence("status", status ?? string.Empty);
        }

        /// <summary>방을 나가거나 연결이 끊겼을 때 호출. 친구 목록의 참가 코드와 상태를 지운다.</summary>
        public static void ClearRoomPresence()
        {
            if (!Initialized) return;
            SteamFriends.ClearRichPresence();
        }

        /// <summary>기다리고 있는 Steam 초대 코드를 가져간다 (한 번 가져가면 사라짐).</summary>
        public static bool TryConsumePendingJoin(out string code)
        {
            code = null;
            if (string.IsNullOrEmpty(pendingJoinCode)) return false;
            bool alive = Time.realtimeSinceStartup <= pendingExpireTime;
            string pending = pendingJoinCode;
            pendingJoinCode = null;
            if (!alive) return false;
            code = pending;
            return true;
        }

        private static void onJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            string code = normalizeCode(data.m_rgchConnect);
            if (string.IsNullOrEmpty(code))
            {
                Debug.LogWarning($"[Steam] 알 수 없는 참가 정보: '{data.m_rgchConnect}'");
                return;
            }
            setPending(code, true);
            JoinRequested?.Invoke(code);
        }

        private static void readCommandLine() //Steam 이 게임을 새로 실행하면서 "+connect 코드" 를 넘기는 경우
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "+connect") continue;
                setPending(normalizeCode(args[i + 1]), false);
                return;
            }
        }

        private static void setPending(string code, bool expires)
        {
            if (string.IsNullOrEmpty(code)) return;
            pendingJoinCode = code;
            pendingExpireTime = expires ? Time.realtimeSinceStartup + CallbackJoinExpireSeconds : float.MaxValue;
        }

        private static string normalizeCode(string raw) //방 코드는 소문자 영숫자 8자리 (NetworkBootstrap.GenerateInviteCode)
        {
            string code = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (code.Length != 8) return string.Empty;
            foreach (char c in code)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return string.Empty;
            return code;
        }
    }
}