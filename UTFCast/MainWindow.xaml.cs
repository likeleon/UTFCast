using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using UTFCast.SimpleHelpers;

namespace UTFCast
{
    public class PropertyNotifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class File : PropertyNotifier
    {
        public enum StateType
        {
            Detected,
            Converted,
            Failed
        }

        public string FullPath { get; set; }
        public string EncodingName { get; set; }
        public bool HasBOM { get; set; }
        public StateType State
        {
            get { return this.state; }
            set
            {
                this.state = value;
                OnPropertyChanged("State");
            }
        }
        public string ErrorMessage { get; set; }

        private StateType state = StateType.Detected;
    }

    public class WorkerOption
    {
        public string Directory { get; set; }
        public bool Recursive { get; set; }
        public bool WriteBom { get; set; }
        public bool DetectOnly { get; set; }
    }


    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };
        
        public ICommand BrowseDirectoryCommand { get; private set; }
        public ICommand StartStopCommand { get; private set; }

        public string Directory { get; set; }
        public string FilePattern { get; set; }
        public bool Recursive { get; set; }
        public bool WriteBOM { get; set; }
        public bool DetectOnly { get; set; }
        public ObservableCollection<File> Files { get; private set; }
        public bool IsStarted 
        {
            get { return this.isStarted; } 
            private set
            {
                this.isStarted = value;
                RaisePropertyChanged("IsStarted");
                RaisePropertyChanged("StartStopButtonText");
            }
        }
        public string StartStopButtonText
        {
            get
            {
                return IsStarted ? "Stop" : "Start";
            }
        }

        private bool isStarted;
        private BackgroundWorker worker = new BackgroundWorker();

        public MainWindow()
        {
            InitializeComponent();

            BrowseDirectoryCommand = new DelegateCommand(BrowseDirectory);
            StartStopCommand = new DelegateCommand(StartOrStop);

            Directory = string.Empty;
            FilePattern = "*.*";
            Recursive = true;
            WriteBOM = true;
            DetectOnly = false;
            Files = new ObservableCollection<File>();
            IsStarted = false;

            this.worker.WorkerReportsProgress = true;
            this.worker.WorkerSupportsCancellation = true;
            this.worker.DoWork += worker_DoWork;
            this.worker.ProgressChanged += worker_ProgressChanged;
            this.worker.RunWorkerCompleted += worker_RunWorkerCompleted;

            DataContext = this;
        }

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            handler(this, new PropertyChangedEventArgs(propertyName));
        }

        private void BrowseDirectory()
        {
            var dialog = new FolderBrowserDialog()
            {
                SelectedPath = Directory,
                ShowNewFolderButton = false,
                Description = "Select directory"
            };
            
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            Directory = dialog.SelectedPath;
            RaisePropertyChanged("Directory");
        }

        private void StartOrStop ()
        {
            if (IsStarted)
            {
                this.worker.CancelAsync();
            }
            else
            {
                Files.Clear();

                WorkerOption workerOption = new WorkerOption()
                {
                    Directory = this.Directory,
                    Recursive = this.Recursive,
                    WriteBom = this.WriteBOM,
                    DetectOnly = this.DetectOnly
                };
                worker.RunWorkerAsync(workerOption);

                IsStarted = true;
            }
        }

        private void worker_DoWork(object sender, DoWorkEventArgs eventArgs)
        {
            BackgroundWorker worker = sender as BackgroundWorker;
            WorkerOption workerOption = eventArgs.Argument as WorkerOption;
            
            SearchOption searchOption = workerOption.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] allFiles = System.IO.Directory.GetFiles(workerOption.Directory, FilePattern, searchOption);
            
            foreach (string filePath in allFiles)
            {
                if (worker.CancellationPending)
                {
                    eventArgs.Cancel = true;
                    break;
                }

                File file = new File()
                {
                    FullPath = System.IO.Path.GetFullPath(filePath),
                    EncodingName = FileEncoding.DetectFileEncoding(filePath),
                    HasBOM = FileEncoding.HasByteOrderMarkUtf8(filePath),
                    State = File.StateType.Detected
                };

                if (!workerOption.DetectOnly)
                {
                    try
                    {
                        Encoding sourceEncoding = GuessFileEncoding(file.FullPath);
                        ChangeFileEncoding(file.FullPath, sourceEncoding, Encoding.UTF8, workerOption.WriteBom);
                        file.State = File.StateType.Converted;
                    }
                    catch (Exception e)
                    {
                        file.ErrorMessage = e.Message;
                        file.State = File.StateType.Failed;
                    }
                }

                worker.ReportProgress(0, file);
            }
        }

        private static Encoding GuessFileEncoding(string file)
        {
            try
            {
                byte[] buffer = new byte[5];
                using (FileStream fileStream = new FileStream(file, FileMode.Open))
                {
                    fileStream.Read(buffer, 0, 5);
                }

                if (buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf)
                    return Encoding.UTF8;
                else if (buffer[0] == 0xfe && buffer[1] == 0xff)
                    return Encoding.Unicode;
                else if (buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xfe && buffer[3] == 0xff)
                    return Encoding.UTF32;
                else if (buffer[0] == 0x2b && buffer[1] == 0x2f && buffer[2] == 0x76)
                    return Encoding.UTF7;
                else
                    return Encoding.Default;
            }
            catch
            {
                return Encoding.Default;
            }
        }

        private void ChangeFileEncoding(string file, Encoding sourceEncoding, Encoding targetEncoding, bool writeBom)
        {
            string content;
            using (StreamReader reader = new StreamReader(file, sourceEncoding, false))
            {
                content = reader.ReadToEnd();
            }

            using (StreamWriter writer = new StreamWriter(file, false, targetEncoding))
            {
                if (writeBom)
                    writer.Write("\xfeff");

                writer.Write(content);
                writer.Flush();
            }
        }

        private void worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            File newFile = e.UserState as File;
            Files.Add(newFile);
        }

        private void worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            IsStarted = false;
        }
    }
}
