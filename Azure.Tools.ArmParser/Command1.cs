using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Azure.Tools.ArmParser
{
    internal sealed class Command1
    {
        public const int CommandId = 0x0100;

        public static readonly Guid CommandSet = new Guid("b394839a-d886-44d2-94c9-ffeeb48d97d5");

        private readonly Package package;

        private Command1(Package package)
        {
            if (package == null)
            {
                throw new ArgumentNullException("package");
            }


            if (ServiceProvider.GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var menuCommandID = new CommandID(CommandSet, CommandId);
                var menuItem = new MenuCommand(this.MenuItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);
            }
        }

        public static Command1 Instance
        {
            get;
            private set;
        }

        private IServiceProvider ServiceProvider
        {
            get
            {
                return package;
            }
        }

        public static void Initialize(Package package)
        {
            Instance = new Command1(package);
        }

        private void MenuItemCallback(object sender, EventArgs e)
        {
            var dte = (EnvDTE.DTE)this.ServiceProvider.GetService(typeof(EnvDTE.DTE));
            var selectedItems = dte.SelectedItems;

            if (selectedItems != null)
            {
                foreach (EnvDTE.SelectedItem selectedItem in selectedItems)
                {
                    var projectItem = selectedItem.ProjectItem as EnvDTE.ProjectItem;

                    if (projectItem != null)
                    {
                        try
                        {
                            ParseArmSource.Execute(projectItem.FileNames[0]);
                        }
                        catch (Exception ex)
                        {
                            var message = $"{projectItem.Name} has Exception: {ex.Message}";


                            // Show a message box to prove we were here
                            VsShellUtilities.ShowMessageBox(
                                this.ServiceProvider,
                                message,
                                "Error",
                                OLEMSGICON.OLEMSGICON_WARNING,
                                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                        }
                    }
                }
            }
        }
    }
}
