using System.Collections.Generic;
using Blocks.Common;
using Blocks.Sessions.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Sessions
{
    public class SessionBrowserController : MonoBehaviour
    {
        private const string k_SessionListName = "session-list";
        private const string k_JoinButtonName = "join-button";
        private const string k_RefreshButtonName = "refresh-button";
        private const string k_SessionNameLabel = "SessionNameLabel";
        private const string k_SessionPlayerCountLabel = "SessionPlayerCountLabel";
        private const string k_NoSessionFoundText = "No sessions found";

        [SerializeField] SessionSettings m_SessionSettings;
        [SerializeField] int m_MaxSessionsDisplayed = 20;

        SessionBrowserViewModel m_ViewModel;
        ListView m_SessionList;
        Button m_JoinButton;
        Button m_RefreshButton;

        void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            m_SessionList = root.Q<ListView>(k_SessionListName);
            m_JoinButton = root.Q<Button>(k_JoinButtonName);
            m_RefreshButton = root.Q<Button>(k_RefreshButtonName);

            m_SessionList.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            m_SessionList.fixedItemHeight = 56f;
            m_SessionList.makeNoneElement = MakeNoneElement;
            m_SessionList.makeItem = MakeItem;
            m_SessionList.bindItem = BindItem;
            m_SessionList.selectedIndicesChanged += OnListSelectionChanged;

            m_SessionList.AddToClassList(BlocksTheme.ScrollView);
            m_SessionList.AddToClassList(BlocksTheme.SpaceBottom);
            m_JoinButton.AddToClassList(BlocksTheme.Button);
            m_JoinButton.AddToClassList(BlocksTheme.SpaceRight);
            m_RefreshButton.AddToClassList(BlocksTheme.Button);

            var buttonsContainer = m_JoinButton.parent;
            buttonsContainer.AddToClassList(BlocksTheme.ContainerHorizontal);
            buttonsContainer.AddToClassList(BlocksTheme.ContainerAlignedRight);

            m_JoinButton.clicked += OnJoinClicked;
            m_RefreshButton.clicked += OnRefreshClicked;

            m_ViewModel = new SessionBrowserViewModel(m_SessionSettings?.sessionType);
            m_ViewModel.SessionsChanged += RefreshList;
            m_ViewModel.StateChanged += RefreshButtonStates;

            RefreshButtonStates();
        }

        void OnDestroy()
        {
            if (m_ViewModel == null) return;

            m_SessionList.selectedIndicesChanged -= OnListSelectionChanged;
            m_JoinButton.clicked -= OnJoinClicked;
            m_RefreshButton.clicked -= OnRefreshClicked;
            m_ViewModel.SessionsChanged -= RefreshList;
            m_ViewModel.StateChanged -= RefreshButtonStates;
            m_ViewModel.Dispose();
            m_ViewModel = null;
        }

        void OnRefreshClicked()
        {
            m_SessionList.ClearSelection();
            _ = m_ViewModel.UpdateSessionListAsync(m_MaxSessionsDisplayed);
        }

        void OnJoinClicked()
        {
            if (!m_ViewModel.SelectedAndAvailable) return;
            _ = m_ViewModel.JoinSessionAsync(m_SessionSettings.ToJoinSessionOptions());
        }

        void OnListSelectionChanged(IEnumerable<int> indices)
        {
            int index = -1;
            foreach (var i in indices) { index = i; break; }
            m_ViewModel.SelectedSessionIndex = index;
        }

        void RefreshList()
        {
            m_SessionList.itemsSource = m_ViewModel.Sessions;
            m_SessionList.Rebuild();
        }

        void RefreshButtonStates()
        {
            m_JoinButton.SetEnabled(m_ViewModel.SelectedAndAvailable);
            m_RefreshButton.SetEnabled(m_ViewModel.CanRefresh);
            m_SessionList.SetSelectionWithoutNotify(new[] { m_ViewModel.SelectedSessionIndex });
        }

        static VisualElement MakeNoneElement()
        {
            var label = new Label(k_NoSessionFoundText);
            label.AddToClassList(BlocksTheme.Label);
            label.AddToClassList(BlocksTheme.SpaceLeft);
            return label;
        }

        static VisualElement MakeItem()
        {
            var container = new VisualElement();
            container.AddToClassList(BlocksTheme.ContainerHorizontal);
            container.AddToClassList(BlocksTheme.ScrollViewElement);
            container.AddToClassList(BlocksTheme.ContainerSpaceBetween);

            var nameLabel = new Label { name = k_SessionNameLabel };
            nameLabel.AddToClassList(BlocksTheme.Label);
            nameLabel.AddToClassList(BlocksTheme.SpaceLeft);
            container.Add(nameLabel);

            var countLabel = new Label { name = k_SessionPlayerCountLabel };
            countLabel.AddToClassList(BlocksTheme.Label);
            countLabel.AddToClassList(BlocksTheme.SpaceRight);
            container.Add(countLabel);

            return container;
        }

        void BindItem(VisualElement element, int index)
        {
            if (index < 0 || index >= m_ViewModel.Sessions.Count) return;
            var session = m_ViewModel.Sessions[index];
            element.Q<Label>(k_SessionNameLabel).text = session.Name;
            element.Q<Label>(k_SessionPlayerCountLabel).text =
                $"{session.MaxPlayers - session.AvailableSlots}/{session.MaxPlayers} Players";
        }
    }
}
