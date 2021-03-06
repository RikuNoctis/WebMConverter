﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace WebMConverter.Dialogs
{
    public partial class ConverterDialog : Form
    {
        private readonly string _infile;
        private readonly string[] _arguments;
        private FFmpeg _ffmpegProcess;

        private Timer _timer;
        private bool _ended;
        private bool _panic;

        private int _currentPass;
        private bool _twopass;
        private bool _cancelTwopass;

        private readonly bool _needToPipe;
        private FFmpeg _pipeFFmpeg;

        public ConverterDialog(string input, string[] args)
        {
            InitializeComponent();
            pictureStatus.BackgroundImage = StatusImages.Images["Happening"];

            _infile = input;
            _arguments = args;
            _needToPipe = Environment.Is64BitOperatingSystem;

            for (var i = 0; i < args.Length; i++)
            {
                AddInputFileToArguments(ref args[i]);
            }
        }

        private void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data != null)
                boxOutput.Invoke((Action)(() => boxOutput.AppendText('\n' + args.Data)));
        }

        private void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data != null)
                boxOutput.Invoke((Action)(() => boxOutput.AppendText('\n' + args.Data)));
        }

        private void ConverterForm_Load(object sender, EventArgs e)
        {
            string argument = null;
            _twopass = true;
            if (_arguments.Length == 1)
            {
                _twopass = false;
                argument = _arguments[0];
            }

            if (_twopass)
            {
                boxOutput.AppendText(string.Format("{0}Arguments for pass 1: {1}", Environment.NewLine, _arguments[0]));
                boxOutput.AppendText(string.Format("{0}Arguments for pass 2: {1}", Environment.NewLine, _arguments[1]));
            }
            else
                boxOutput.AppendText("\nArguments: " + argument);

            if (_twopass)
                MultiPass(_arguments);
            else
                SinglePass(argument);
        }

        void AddInputFileToArguments(ref string argument)
        {
            if (_needToPipe)
            {
                argument = string.Format(@"-f nut -i pipe:0 {0}", argument);
            }
            else
            {
                argument = string.Format(@"-f avisynth -i ""{0}"" {1}", _infile, argument);
            }
        }

        void StartPipe(FFmpeg ffmpeg)
        {
            if (!_needToPipe)
                return;

            string proxyargs = string.Format(@"-f avisynth -i ""{0}"" -f nut -c copy -v error pipe:1", _infile);
            boxOutput.AppendText("\n--- CREATING AVISYNTH PROXY --- ");

            _pipeFFmpeg = new FFmpeg(proxyargs, true);
            _pipeFFmpeg.ErrorDataReceived += (o, args) =>
            {
                try
                {
                    boxOutput.Invoke((Action) (() =>
                    {
                        boxOutput.AppendText(Environment.NewLine + args.Data);
                    }));
                }
                catch
                {
                    // ignored
                }
            };
            _pipeFFmpeg.Start(false);
            var bw = new BackgroundWorker();
            bw.DoWork += delegate
            {
                try
                {
                    _pipeFFmpeg.StandardOutput.BaseStream.CopyTo(ffmpeg.StandardInput.BaseStream);
                }
                catch
                {
                    // ignored
                }
            };
            _pipeFFmpeg.Exited += delegate
            {
                try
                {
                    _ffmpegProcess.StandardInput.Close();
                }
                catch
                {
                    // ignored
                }
            };
            bw.RunWorkerAsync();
        }

        private void SinglePass(string argument)
        {
            _ffmpegProcess = new FFmpeg(argument);

            _ffmpegProcess.ErrorDataReceived += ProcessOnErrorDataReceived;
            _ffmpegProcess.OutputDataReceived += ProcessOnOutputDataReceived;
            _ffmpegProcess.Exited += (o, args) => boxOutput.Invoke((Action)(() =>
            {
                if (_panic) return; //This should stop that one exception when closing the converter
                boxOutput.AppendText("\n--- FFMPEG HAS EXITED ---");
                buttonCancel.Enabled = false;

                _timer = new Timer();
                _timer.Interval = 500;
                _timer.Tick += Exited;
                _timer.Start();
            }));

            _ffmpegProcess.Start();
            StartPipe(_ffmpegProcess);
        }

        private void MultiPass(string[] arguments)
        {
            int passes = arguments.Length;

            _ffmpegProcess = new FFmpeg(arguments[_currentPass]);

            _ffmpegProcess.ErrorDataReceived += ProcessOnErrorDataReceived;
            _ffmpegProcess.OutputDataReceived += ProcessOnOutputDataReceived;
            _ffmpegProcess.Exited += (o, args) => boxOutput.Invoke((Action)(() =>
            {
                if (_panic) return; //This should stop that one exception when closing the converter
                boxOutput.AppendText("\n--- FFMPEG HAS EXITED ---");

                _currentPass++;
                if (_currentPass < passes && !_cancelTwopass)
                {
                    boxOutput.AppendText(string.Format("\n--- ENTERING PASS {0} ---", _currentPass + 1));

                    MultiPass(arguments); //Sort of recursion going on here, be careful with stack overflows and shit
                    return;
                }

                buttonCancel.Enabled = false;

                _timer = new Timer();
                _timer.Interval = 500;
                _timer.Tick += Exited;
                _timer.Start();
            }));

            _ffmpegProcess.Start();
            StartPipe(_ffmpegProcess);
        }

        private void Exited(object sender, EventArgs eventArgs)
        {
            _timer.Stop();

            var process = _ffmpegProcess;

            if (process.ExitCode != 0)
            {
                if (_cancelTwopass)
                    boxOutput.AppendText("\n\nConversion cancelled.");
                else
                {
                    boxOutput.AppendText(string.Format("\n\nffmpeg.exe exited with exit code {0}. That's usually bad.", process.ExitCode));
                    boxOutput.AppendText("\nIf you have no idea what went wrong, open an issue on GitHub and copy paste the output of this window there.");
                }
                pictureStatus.BackgroundImage = StatusImages.Images["Failure"];

                if (process.ExitCode == -1073741819) //This error keeps happening for me if I set threads to anything above 1, might happen for other people too
                    MessageBox.Show("It appears ffmpeg.exe crashed because of a thread error. Set the amount of threads to 1 in the advanced tab and try again.", "FYI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                boxOutput.AppendText("\n\nVideo converted succesfully!");
                pictureStatus.BackgroundImage = StatusImages.Images["Success"];

                buttonPlay.Enabled = true;
            }

            buttonCancel.Text = "Close";
            buttonCancel.Enabled = true;
            _ended = true;
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            _cancelTwopass = true;

            if (!_ended || _panic) //Prevent stack overflow
            {
                if (!_ffmpegProcess.HasExited)
                    _ffmpegProcess.Kill();

                if (!_needToPipe)
                    return;

                if (!_pipeFFmpeg.HasExited)
                    _pipeFFmpeg.Kill();
            }
            else
                Close();
        }

        private void ConverterForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _panic = true; //Shut down while avoiding exceptions
            buttonCancel_Click(sender, e);
        }

        private void ConverterForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _ffmpegProcess.Dispose();
        }

        private void buttonPlay_Click(object sender, EventArgs e)
        {
            Process.Start(((MainForm) Owner).textBoxOut.Text); //Play result video
        }
    }
}
