using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Dockyard.Models
{
    /// <summary>One launcher tile in the dock.</summary>
    public class DockItem : INotifyPropertyChanged
    {
        private string _name = "";
        private string _path = "";
        private string _arguments = "";
        private string _workingDirectory = "";
        private string _iconSource = "";
        private ImageSource _icon;

        /// <summary>Label shown under the icon.</summary>
        public string Name
        {
            get => _name;
            set { _name = value; Raise(); }
        }

        /// <summary>What gets launched. An .exe, a document, a URL, or "shell:AppsFolder\..." for a Store app.</summary>
        public string Path
        {
            get => _path;
            set { _path = value; Raise(); }
        }

        public string Arguments
        {
            get => _arguments;
            set { _arguments = value; Raise(); }
        }

        public string WorkingDirectory
        {
            get => _workingDirectory;
            set { _workingDirectory = value; Raise(); }
        }

        /// <summary>
        /// Where the icon is pulled from. Usually the same as Path, but a shortcut may point its
        /// icon elsewhere, and the user can override it with any .png / .ico / .exe.
        /// </summary>
        public string IconSource
        {
            get => _iconSource;
            set { _iconSource = value; Raise(); }
        }

        [JsonIgnore]
        public ImageSource Icon
        {
            get => _icon;
            set { _icon = value; Raise(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
