namespace SyncPro.UI.Navigation.ViewModels
{
    using System;

    using SyncPro.Runtime;

    public class SyncJobNodeViewModel : NavigationNodeViewModel
    {
        private readonly SyncJobPanelViewModel syncJobPanel;

        public SyncJobNodeViewModel(NavigationNodeViewModel parent, SyncJobPanelViewModel syncJobPanel) 
            : base(parent, syncJobPanel)
        {
            this.syncJobPanel = syncJobPanel;

            if (syncJobPanel.SyncJobViewModel != null)
            {
                this.Name = syncJobPanel.SyncJobViewModel.StartTime.ToString("g");
            }

            this.SetIconImage();
        }

        private void SetIconImage()
        {
            if (this.syncJobPanel.SyncJobViewModel == null)
            {
                this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/list_16.png";
                return;
            }

            switch (this.syncJobPanel.SyncJobViewModel.SyncJobResult)
            {
                case SyncJobResult.Success:
                    this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/list_ok_16.png";
                    break;
                case SyncJobResult.Warning:
                    this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/list_warn_16.png";
                    break;
                case SyncJobResult.Error:
                    this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/list_error_16.png";
                    break;
                case SyncJobResult.NotRun:
                    this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/list_16.png";
                    break;
                default:
                    this.IconImageSource = "/SyncPro.UI;component/Resources/Graphics/list_help_16.png";
                    break;
            }
        }

        protected override void OnIsSelected()
        {
            this.syncJobPanel.SyncJobViewModel?.BeginLoad();
        }
    }
}