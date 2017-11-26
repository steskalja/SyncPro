﻿namespace SyncPro.UI.Navigation.ViewModels
{
    using System.Collections.Specialized;
    using System.Linq;

    using SyncPro.UI.ViewModels;

    public class SyncHistoryNodeViewModel : NavigationNodeViewModel
    {
        private readonly SyncRelationshipViewModel relationship;

        public SyncHistoryNodeViewModel(NavigationNodeViewModel parent, SyncRelationshipViewModel relationship) 
            : base(parent, relationship)
        {
            this.relationship = relationship;
            this.Name = "Synchronization History";
            this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/history_16.png";

            foreach (SyncRunViewModel syncRunViewModel in relationship.SyncRunHistory)
            {
                this.AddSyncRunHistory(syncRunViewModel);
            }

            relationship.SyncRunHistory.CollectionChanged += (sender, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                {
                    foreach (SyncRunViewModel syncRunViewModel in args.NewItems.OfType<SyncRunViewModel>())
                    {
                        App.DispatcherInvoke(() =>
                        {
                            this.AddSyncRunHistory(syncRunViewModel);
                        });
                    }
                }
            };
        }

        private void AddSyncRunHistory(SyncRunViewModel syncRunViewModel)
        {
            SyncRunPanelViewModel syncRunPanel = new SyncRunPanelViewModel(this.relationship) { SyncRun = syncRunViewModel };
            this.Children.Add(new SyncRunNodeViewModel(this, syncRunPanel));
        }
    }
}