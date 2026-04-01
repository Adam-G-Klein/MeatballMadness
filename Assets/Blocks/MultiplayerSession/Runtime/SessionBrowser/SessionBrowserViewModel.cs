using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Blocks.Sessions
{
    public class SessionBrowserViewModel : IDisposable
    {
        private SessionObserver m_SessionObserver;
        private ServiceObserver<IMultiplayerService> m_ServiceObserver;

        private bool m_SelectedAndAvailable;
        private bool m_CanRefresh;
        private int m_SelectedSessionIndex;
        private ISession m_Session;

        public event Action SessionsChanged;
        public event Action StateChanged;

        public List<SessionInfoViewModel> Sessions { get; private set; }

        public int SelectedSessionIndex
        {
            get => m_SelectedSessionIndex;
            set
            {
                if (value >= 0 && value < Sessions.Count)
                {
                    m_SelectedSessionIndex = value;
                    SelectedAndAvailable = true;
                }
                else
                {
                    SelectedAndAvailable = false;
                }
            }
        }

        public bool SelectedAndAvailable
        {
            get => m_SelectedAndAvailable;
            private set
            {
                var newValue = value;
                if (value && m_Session != null && m_Session.Id == GetSelectedSessionId())
                    newValue = false;

                if (m_SelectedAndAvailable != newValue)
                {
                    m_SelectedAndAvailable = newValue;
                    StateChanged?.Invoke();
                }
            }
        }

        public bool CanRefresh
        {
            get => m_CanRefresh;
            private set
            {
                if (m_CanRefresh == value) return;
                m_CanRefresh = value;
                StateChanged?.Invoke();
            }
        }

        public SessionBrowserViewModel(string sessionType)
        {
            Sessions = new List<SessionInfoViewModel>();

            m_SessionObserver = new SessionObserver(sessionType);
            m_SessionObserver.SessionAdded += OnSessionAdded;

            if (m_SessionObserver.Session != null)
                OnSessionAdded(m_SessionObserver.Session);

            if (UnityServices.Instance != null)
            {
                m_ServiceObserver = new ServiceObserver<IMultiplayerService>();
                if (m_ServiceObserver.Service != null)
                {
                    CanRefresh = true;
                }
                else
                {
                    m_ServiceObserver.Initialized += OnServicesInitialized;
                }
            }
        }

        public string GetSelectedSessionId()
        {
            if (m_SelectedSessionIndex >= 0 && m_SelectedSessionIndex < Sessions.Count)
                return Sessions[m_SelectedSessionIndex].Id;
            return null;
        }

        public async Task JoinSessionAsync(JoinSessionOptions options)
        {
            try
            {
                await MultiplayerService.Instance.JoinSessionByIdAsync(GetSelectedSessionId(), options);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        public async Task UpdateSessionListAsync(int numberOfMaxSessions)
        {
            if (!CanRefresh)
            {
                Debug.LogWarning("Cannot refresh session list. Multiplayer Services are not initialized.");
                return;
            }

            try
            {
                CanRefresh = false;
                var queryResult = await MultiplayerService.Instance
                    .QuerySessionsAsync(new QuerySessionsOptions
                    {
                        SortOptions = new List<SortOption>
                        {
                            new(SortOrder.Descending, SortField.Name)
                        }
                    });

                foreach (var session in Sessions)
                    session.Dispose();

                Sessions.Clear();
                for (var i = 0; i < Math.Min(queryResult.Sessions.Count, numberOfMaxSessions); i++)
                    Sessions.Add(new SessionInfoViewModel(queryResult.Sessions[i]));

                SelectedSessionIndex = -1;
                CanRefresh = true;
                SessionsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to update session list: {ex.Message}");
                CanRefresh = true;
            }
        }

        private void OnServicesInitialized(IMultiplayerService service)
        {
            m_ServiceObserver.Initialized -= OnServicesInitialized;
            CanRefresh = true;
        }

        private void OnSessionAdded(ISession newSession)
        {
            m_Session = newSession;
            m_Session.RemovedFromSession += OnSessionRemoved;
            m_Session.Deleted += OnSessionRemoved;
            if (m_Session.Id == GetSelectedSessionId())
                SelectedAndAvailable = false;
        }

        private void OnSessionRemoved()
        {
            var lastSessionId = m_Session.Id;
            CleanupSession();
            if (lastSessionId == GetSelectedSessionId())
                SelectedAndAvailable = true;
        }

        private void CleanupSession()
        {
            m_Session.RemovedFromSession -= OnSessionRemoved;
            m_Session.Deleted -= OnSessionRemoved;
            m_Session = null;
        }

        public void Dispose()
        {
            if (m_SessionObserver != null)
            {
                m_SessionObserver.SessionAdded -= OnSessionAdded;
                m_SessionObserver.Dispose();
                m_SessionObserver = null;
            }

            if (m_ServiceObserver != null)
            {
                m_ServiceObserver.Dispose();
                m_ServiceObserver = null;
            }

            if (m_Session != null)
                CleanupSession();
        }
    }
}
