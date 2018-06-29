using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Azure.Tools.ArmParser
{
    internal sealed class Command1
    {
        public const int CommandId = 0x0100;

        public static readonly Guid CommandSet = new Guid("b394839a-d886-44d2-94c9-ffeeb48d97d5");

        private readonly Package Package;

        private Command1(Package package)
        {
            if (package == null)
            {
                throw new ArgumentNullException("package");
            }

            Package = package;


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
                return Package;
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
                    var projectItem = selectedItem.ProjectItem as ProjectItem;

                    if (projectItem != null)
                    {
                        try
                        {
                            var doc = projectItem.Document;
                            if (!projectItem.IsOpen)
                            {
                                projectItem.Open();
                            }


                            var textSelection = (TextSelection)projectItem.Document.Selection;
                            textSelection.SelectAll();

                            var currentText = textSelection.Text;
                            var newText = ParseArmSource.Execute(textSelection.Text);

                            if (currentText.Equals(newText))
                            {
                                VsShellUtilities.ShowMessageBox(
                                                               this.ServiceProvider,
                                                               $"Done parsing {projectItem.Document.Name}",
                                                               "No changes!",
                                                               OLEMSGICON.OLEMSGICON_INFO,
                                                               OLEMSGBUTTON.OLEMSGBUTTON_OK,
                                                               OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                            }                            
                            else
                            {
                                textSelection.Insert(ParseArmSource.Execute(textSelection.Text));
                                if (VsShellUtilities.ShowMessageBox(
                                                               this.ServiceProvider,
                                                               $"Done parsing {projectItem.Document.Name}",
                                                               "Save changes?",
                                                               OLEMSGICON.OLEMSGICON_QUERY,
                                                               OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                                                               OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == 6)

                                {
                                    projectItem.Document.Save();
                                    //projectItem.Save();
                                }
                            }



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
