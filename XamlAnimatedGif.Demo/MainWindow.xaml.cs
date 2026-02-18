using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace XamlAnimatedGif.Demo
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public MainWindow()
        {
            InitializeComponent();

            //AnimationBehavior.SetDownloadCacheLocation(@"C:\GifCache"); //Path.GetTempPath()

            _images = new ObservableCollection<string>
                      {
                          "images/working.gif",
                          "images/earth.gif",
                          "images/radar.gif",
                          "images/bomb.gif",
                          "images/bomb-once.gif",
                          "images/nonanimated.gif",
                          "images/monster.gif",
                          "pack://siteoforigin:,,,/images/siteoforigin.gif",
                          "images/partialfirstframe.gif",
                          "images/newton-cradle.gif",
                          "images/not-a-gif.png",
                          "images/optimized-full-code-table.gif",
                          "images/corrupt-frame-size.gif",
                          "images/interlaced.gif",
                          "http://i.imgur.com/rCK6xzh.gif",
                          "http://media.giphy.com/media/zW2pe7UscHiq4/giphy.gif",
                          "http://media.giphy.com/media/nWn6ko2ygIeEU/giphy.gif"
                      };
            DataContext = this;
        }


#pragma warning disable IDE1006 // Naming Styles
        private void btnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog {Filter = "GIF images|*.gif"};
            if (dlg.ShowDialog() == true)
            {
                Images.Add(dlg.FileName);
                SelectedImage = dlg.FileName;
            }
        }

        private ObservableCollection<string> _images;
        public ObservableCollection<string> Images
        {
            get => _images;
            set
            {
                _images = value;
                OnPropertyChanged();
            }
        }

        private string _selectedImage;
        public string SelectedImage
        {
            get => _selectedImage;
            set
            {
                ClearStopwatch();
                _selectedImage = value;
                OnPropertyChanged();
                Completed = false;
            }
        }

        private TimeSpan? _lastRunTime;
        public TimeSpan? LastRunTime
        {
            get => _lastRunTime;
            set
            {
                _lastRunTime = value;
                OnPropertyChanged(nameof(LastRunTime));
            }
        }


        private async void AnimationBehavior_OnLoaded(object sender, RoutedEventArgs e)
        {
            IsDownloading = false;

            if (_animator != null)
            {
                _animator.CurrentFrameChanged -= CurrentFrameChanged;
            }

            _animator = await AnimationBehavior.GetAnimatorTask(img);

            if (_animator != null)
            {
                _animator.CurrentFrameChanged += CurrentFrameChanged;
                sldPosition.Value = 0;
                sldPosition.Maximum = _animator.FrameCount - 1;
                SetPlayPauseEnabled(_animator.IsPaused || _animator.IsComplete);
            }
        }

        private Stopwatch _stopwatch;
        private void CurrentFrameChanged(object sender, EventArgs e)
        {
            Animator animator = Volatile.Read(in _animator);
            if (animator is not null)
            {
                if (animator.CurrentFrameIndex == 0)
                {
                    StopStopwatch();
                    if (!animator.IsPaused && !animator.IsComplete)
                        StartStopwatch();
                }

                sldPosition.Value = animator.CurrentFrameIndex;
            }
        }

        private void StartStopwatch()
        {
            _stopwatch ??= new Stopwatch();
            _stopwatch.Restart();
        }

        private void PauseStopwatch() => _stopwatch.Stop();

        private void ResumeStopwatch() => _stopwatch?.Start();

        private void StopStopwatch()
        {
            var stopwatch = Volatile.Read(in _stopwatch);
            if (stopwatch is not null)
            {
                stopwatch.Stop();
                LastRunTime = stopwatch.Elapsed;
            }
        }

        private void ClearStopwatch()
        {
            _stopwatch?.Stop();
            LastRunTime = null;
        }

        private void AnimationBehavior_OnAnimationStarted(DependencyObject d, AnimationStartedEventArgs e)
        {
            StartStopwatch();
        }

        private void AnimationBehavior_OnAnimationCompleted(DependencyObject sender, AnimationCompletedEventArgs e)
        {
            StopStopwatch();
            Completed = true;
            var animator = Volatile.Read(in _animator);
            if (animator != null)
                SetPlayPauseEnabled(animator.IsPaused || animator.IsComplete);
        }

        private bool _useDefaultRepeatBehavior = true;
        public bool UseDefaultRepeatBehavior
        {
            get => _useDefaultRepeatBehavior;
            set
            {
                Interlocked.Exchange(ref _useDefaultRepeatBehavior, value);
                OnPropertyChanged();
                if (value)
                    RepeatBehavior = default;
            }
        }


        private bool _repeatForever;
        public bool RepeatForever
        {
            get => _repeatForever;
            set
            {
                Interlocked.Exchange(ref _repeatForever, value);
                OnPropertyChanged();
                if (value)
                    RepeatBehavior = RepeatBehavior.Forever;
            }
        }


        private bool _useSpecificRepeatCount;
        public bool UseSpecificRepeatCount
        {
            get => _useSpecificRepeatCount;
            set
            {
                Interlocked.Exchange(ref _useSpecificRepeatCount, value);
                OnPropertyChanged();
                if (value)
                    RepeatBehavior = new RepeatBehavior(RepeatCount);
            }
        }

        private int _repeatCount = 3;
        public int RepeatCount
        {
            get => _repeatCount;
            set
            {
                Interlocked.Exchange(ref _repeatCount, value);
                OnPropertyChanged();
                if (UseSpecificRepeatCount)
                    RepeatBehavior = new RepeatBehavior(value);
            }
        }

        private bool _completed;
        public bool Completed
        {
            get => _completed;
            set
            {
                Interlocked.Exchange(ref _completed, value);
                OnPropertyChanged();
            }
        }

        private RepeatBehavior _repeatBehavior;
        public RepeatBehavior RepeatBehavior
        {
            get => _repeatBehavior;
            set
            {
                _repeatBehavior = value;
                OnPropertyChanged();
                Completed = false;
            }
        }

        private bool _autoStart = true;
        public bool AutoStart
        {
            get => _autoStart;
            set
            {
                Interlocked.Exchange(ref _autoStart, value);
                OnPropertyChanged();
            }
        }

        private bool _cacheFramesInMemory;

        public bool CacheFramesInMemory
        {
            get => _cacheFramesInMemory;
            set
            {
                Interlocked.Exchange(ref _cacheFramesInMemory, value);
                OnPropertyChanged();
            }
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set
            {
                Interlocked.Exchange(ref _isDownloading, value);
                OnPropertyChanged();
            }
        }

        private int _downloadProgress;
        public int DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                Interlocked.Exchange(ref _downloadProgress, value);
                OnPropertyChanged();
            }
        }

        private bool _isDownloadProgressIndeterminate;
        public bool IsDownloadProgressIndeterminate
        {
            get => _isDownloadProgressIndeterminate;
            set
            {
                Interlocked.Exchange(ref _isDownloadProgressIndeterminate, value);
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private Animator _animator;

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            PauseStopwatch();
            _animator?.Pause();
            SetPlayPauseEnabled(true);
        }

        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            ResumeStopwatch();
            _animator?.Play();
            Completed = false;
            SetPlayPauseEnabled(false);
        }

        private void sldPosition_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Not supported (yet?)

            //if (_animator != null)
            //{
            //    var currentFrame = _animator.CurrentFrameIndex;
            //    if (currentFrame >= 0 && currentFrame != (int)sldPosition.Value)
            //        _animator.GotoFrame((int)sldPosition.Value);
            //}
        }

        private void SetPlayPauseEnabled(bool isPaused)
        {
            btnPause.IsEnabled = !isPaused;
            btnPlay.IsEnabled = isPaused;
            btnRewind.IsEnabled = true;
        }

        private void btnOpenUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = InputBox.Show("Enter the URL of the image to load", "Enter URL");
            if (!string.IsNullOrEmpty(url))
            {
                Images.Add(url);
                SelectedImage = url;
            }
        }

        private void btnGC_Click(object sender, RoutedEventArgs e)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void btnBasicTests_Click(object sender, RoutedEventArgs e)
        {
            new BasicTestsWindow().ShowDialog();
        }

        private void AnimationBehavior_OnError(DependencyObject d, AnimationErrorEventArgs e)
        {
            if (e.Kind == AnimationErrorKind.Loading)
                IsDownloading = false;

            MessageBox.Show($"An error occurred ({e.Kind}): {e.Exception}");
        }

        private void btnRewind_Click(object sender, RoutedEventArgs e)
        {
            var animator = Volatile.Read(in _animator);
            if (animator == null)
                return;
            animator.Rewind();
            SetPlayPauseEnabled(animator.IsPaused || animator.IsComplete);
            Completed = false;
        }

        private void AnimationBehavior_OnDownloadProgress(DependencyObject d, DownloadProgressEventArgs e)
        {
            IsDownloading = true;
            if (e.Progress >= 0)
            {
                DownloadProgress = e.Progress;
                IsDownloadProgressIndeterminate = false;
            }
            else
            {
                IsDownloadProgressIndeterminate = true;
            }
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            SelectedImage = null;
        }
    }
}
