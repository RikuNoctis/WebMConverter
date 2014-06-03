﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using StopWatch = System.Timers.Timer;
using System.Xml;
using System.Net;
using System.Threading.Tasks;

namespace WebMConverter
{
    public partial class MainForm : Form
    {
        private const string _template = " {1} {2} -f webm -y \"{0}\"";
        //{0} is output file
        //{1} is extra arguments
        //{2} is '-pass X' if HQ mode enabled, otherwise blank

        private const string _templateArguments = "{0} -c:v libvpx -crf 32 -b:v {1}K -threads {2} -slices {3}{4}{5}{6}";
        //{0} is '-an' if no audio, otherwise blank
        //{1} is bitrate in kb/s
        //{2} is amount of threads to use
        //{3} is amount of slices to split the frame into
        //{4} is ' -fs XM' if X MB limit enabled otherwise blank
        //{5} is ' -metadata title="TITLE"' when specifying a title, otherwise blank
        //{6} is ' -quality best -lag-in-frames 16 -auto-alt-ref 1' when using HQ mode, otherwise blank

        private string _indexFile;

        private string _autoOutput;
        private string _autoTitle;
        private string _autoArguments;
        private bool _argumentError;

        bool indexing = false;
        BackgroundWorker indexbw;

        StopWatch toolTipTimer;
        string avsScriptInfo;

        int videotrack = -1;
        int audiotrack = -1;
        bool audioDisabled;

        #region MainForm

        public MainForm()
        {
            FFMSSharp.FFMS2.Initialize(Path.Combine(Environment.CurrentDirectory, "Binaries", "Win32"));

            InitializeComponent();

            ImageList imageList = new ImageList();
            imageList.ImageSize = new Size(100, 100);

            imageList.Images.Add("crop", Properties.Resources.crop);
            imageList.Images.Add("resize", Properties.Resources.resize);
            imageList.Images.Add("reverse", Properties.Resources.reverse);
            imageList.Images.Add("subtitles", Properties.Resources.subtitles);
            imageList.Images.Add("trim", Properties.Resources.trim);

            listViewProcessingScript.LargeImageList = imageList;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            int threads = Environment.ProcessorCount;
            trackThreads.Value = Math.Min(trackThreads.Maximum, Math.Max(trackThreads.Minimum, threads));

            trackThreads_Scroll(sender, e); //Update label
        }

        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            // show copy cursor for files
            bool dataPresent = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = dataPresent ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            SetFile(files[0]);
        }

        async void MainForm_Shown(object sender, EventArgs e)
        {
            clearToolTip();

            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1) // We were "Open with..."ed with a file
                SetFile(args[1]);
            
            await Task.Run(() => CheckUpdate());
        }

        void setToolTip(string message)
        {
            if (this.IsDisposed || toolStripStatusLabel.IsDisposed)
                return;

            if (this.InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate
                {
                    toolStripStatusLabel.Text = message;
                });
            }
            else
            {
                toolStripStatusLabel.Text = message;
            }
        }

        [System.Diagnostics.DebuggerStepThrough]
        public void showToolTip(string message, int timer = 0)
        {
            clearToolTip();

            setToolTip(message);

            if (timer > 0)
            {
                toolTipTimer = new StopWatch(timer);

                toolTipTimer.Elapsed += (sender, e) => clearToolTip();

                toolTipTimer.AutoReset = false;
                toolTipTimer.Enabled = true;
            }
        }

        [System.Diagnostics.DebuggerStepThrough]
        public void clearToolTip(object sender = null, EventArgs e = null)
        {
            if (toolTipTimer != null)
                toolTipTimer.Close();

            setToolTip("");
        }

        #endregion

        #region groupBoxMain

        private void buttonBrowseIn_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.CheckFileExists = true;
                dialog.CheckPathExists = true;
                dialog.ValidateNames = true;

                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
                    SetFile(dialog.FileName);
            }
        }

        private void buttonBrowseOut_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.OverwritePrompt = true;
                dialog.ValidateNames = true;
                dialog.Filter = "WebM files|*.webm";

                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
                    textBoxOut.Text = dialog.FileName;
            }
        }

        private void buttonGo_Click(object sender, EventArgs e)
        {
            if (indexing) // We are actually a cancel button right now.
            {
                indexbw.CancelAsync();
                buttonGo.Enabled = false;
                return;
            }

            try
            {
                Convert();
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message, "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void buttonPreview_Click(object sender, EventArgs e)
        {
            try
            {
                Preview();
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message, "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region tabPageProcessing

        private void toolStripButtonCaption_Click(object sender, EventArgs e)
        {
            using (var form = new CaptionForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.Caption = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Caption", "subtitles"); // todo: get another icon
                        toolStripButtonCaption.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonCrop_Click(object sender, EventArgs e)
        {
            using (var form = new CropForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.Crop = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Crop", "crop");
                        SetSlices();
                        toolStripButtonCrop.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonMultipleTrim_Click(object sender, EventArgs e)
        {
            using (var form = new MultipleTrimForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.MultipleTrim = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Multiple Trim", "trim");
                        GenerateArguments();
                        toolStripButtonTrim.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonOverlay_Click(object sender, EventArgs e)
        {
            using (var form = new OverlayForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.Overlay = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Overlay", "subtitles"); // todo: get another icon
                        toolStripButtonOverlay.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonResize_Click(object sender, EventArgs e)
        {
            using (var form = new ResizeForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.Resize = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Resize", "resize");
                        SetSlices();
                        toolStripButtonResize.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonReverse_Click(object sender, EventArgs e)
        {
            if (toolStripButtonAdvancedScripting.Checked)
            {
                textBoxProcessingScript.AppendText(Environment.NewLine + new ReverseFilter().ToString());
            }
            else
            {
                Filters.Reverse = new ReverseFilter();
                listViewProcessingScript.Items.Add("Reverse", "reverse");
                toolStripButtonReverse.Enabled = false;
            }
        }

        private void toolStripButtonSubtitle_Click(object sender, EventArgs e)
        {
            using (var form = new SubtitleForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.Subtitle = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Subtitle", "subtitles");
                        toolStripButtonSubtitle.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonTrim_Click(object sender, EventArgs e)
        {
            using (var form = new TrimForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (toolStripButtonAdvancedScripting.Checked)
                    {
                        textBoxProcessingScript.AppendText(Environment.NewLine + form.GeneratedFilter.ToString());
                    }
                    else
                    {
                        Filters.Trim = form.GeneratedFilter;
                        listViewProcessingScript.Items.Add("Trim", "trim");
                        GenerateArguments();
                        toolStripButtonTrim.Enabled = false;
                    }
                }
            }
        }

        private void toolStripButtonAdvancedScripting_Click(object sender, EventArgs e)
        {
            ProbeScript();
            toolStripButtonAdvancedScripting.Checked = !toolStripButtonAdvancedScripting.Checked;
            //if (toolStripButton1.Checked)
            //{
            listViewProcessingScript.Hide();
            if (Filters.Caption != null)
                Filters.Caption.BeforeEncode(Program.VideoSource.GetFrame(0).EncodedResolution);
            GenerateAvisynthScript();
            textBoxProcessingScript.Show();
            toolStripFilterButtonsEnabled(true);
            //}
            //else
            //{
            //    textBoxProcessingScript.Hide();
            //    listViewProcessingScript.Show();
            //}
            // This could be used to toggle between advanced and list view, but that's hard to code. Maybe later?
            // For now, just stay in advanced mode forever.
            toolStripButtonAdvancedScripting.Enabled = false;
            clearToolTip(sender, e);
        }

        private void toolStripButtonAdvancedScripting_CheckedChanged(object sender, EventArgs e)
        {
            toolStripButtonAdvancedScripting.Image = toolStripButtonAdvancedScripting.Checked ? WebMConverter.Properties.Resources.tick : WebMConverter.Properties.Resources.cross; // STUB: get better icons
        }

        private void toolStripFilterButtonsEnabled(bool enabled)
        {
            toolStripButtonCaption.Enabled = enabled;
            toolStripButtonCrop.Enabled = enabled;
            toolStripButtonOverlay.Enabled = enabled;
            toolStripButtonResize.Enabled = enabled;
            toolStripButtonReverse.Enabled = enabled;
            toolStripButtonSubtitle.Enabled = enabled;
            toolStripButtonTrim.Enabled = enabled;
        }

        private void listViewProcessingScript_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                foreach (ListViewItem item in listViewProcessingScript.SelectedItems)
                {
                    switch (item.Text)
                    {
                        case "Caption":
                            Filters.Caption = null;
                            toolStripButtonCaption.Enabled = true;
                            break;
                        case "Crop":
                            Filters.Crop = null;
                            toolStripButtonCrop.Enabled = true;
                            SetSlices();
                            break;
                        case "Multiple Trim":
                            Filters.MultipleTrim = null;
                            toolStripButtonTrim.Enabled = true;
                            GenerateArguments();
                            break;
                        case "Overlay":
                            Filters.Overlay = null;
                            toolStripButtonOverlay.Enabled = true;
                            break;
                        case "Resize":
                            Filters.Resize = null;
                            toolStripButtonResize.Enabled = true;
                            SetSlices();
                            break;
                        case "Reverse":
                            Filters.Reverse = null;
                            toolStripButtonReverse.Enabled = true;
                            break;
                        case "Subtitle":
                            Filters.Subtitle = null;
                            toolStripButtonSubtitle.Enabled = true;
                            break;
                        case "Trim":
                            Filters.Trim = null;
                            toolStripButtonTrim.Enabled = true;
                            GenerateArguments();
                            break;
                    }
                    listViewProcessingScript.Items.Remove(item);
                }
            }
        }

        private void listViewProcessingScript_ItemActivate(object sender, EventArgs e)
        {
            switch (listViewProcessingScript.FocusedItem.Text)
            {
                case "Caption":
                    using (var form = new CaptionForm(Filters.Caption))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.Caption = form.GeneratedFilter;
                        }
                    }
                    break;
                case "Crop":
                    using (var form = new CropForm(Filters.Crop))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.Crop = form.GeneratedFilter;
                            SetSlices();
                        }
                    }
                    break;
                case "Multiple Trim":
                    using (var form = new MultipleTrimForm(Filters.MultipleTrim))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.MultipleTrim = form.GeneratedFilter;
                            GenerateArguments();
                        }
                    }
                    break;
                case "Overlay":
                    using (var form = new OverlayForm(Filters.Overlay))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.Overlay = form.GeneratedFilter;
                        }
                    }
                    break;
                case "Resize":
                    using (var form = new ResizeForm(Filters.Resize))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.Resize = form.GeneratedFilter;
                            SetSlices();
                        }
                    }
                    break;
                case "Subtitle":
                    using (var form = new SubtitleForm(Filters.Subtitle))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.Subtitle = form.GeneratedFilter;
                        }
                    }
                    break;
                case "Trim":
                    using (var form = new TrimForm(Filters.Trim))
                    {
                        if (form.ShowDialog(this) == DialogResult.OK)
                        {
                            Filters.Trim = form.GeneratedFilter;
                            GenerateArguments();
                        }
                    }
                    break;
                default:
                    MessageBox.Show("This filter has no options.");
                    break;
            }
        }

        void textBoxProcessingScript_Leave(object sender, EventArgs e)
        {
            ProbeScript();
            SetSlices();
        }

        #region Tooltips

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonTrim_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Select a clip from your video.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonMultipleTrim_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Select many clips from your video, and sort them on a timeline.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonCrop_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Crop your video into a smaller frame.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonResize_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Scale your video.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonReverse_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Everything is backwards!");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonSubtitle_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Burn subtitles into the video.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonOverlay_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Overlay a picture on top of your video.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonCaption_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Add some funny text to your video.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void buttonPreview_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Open a preview window that will loop your processing settings. Note that this doesn't reflect output encoding quality.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void toolStripButtonAdvancedScripting_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Are you a bad enough dude? Take care, there is no way back. You will have to start over if you fuck up.");
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void listViewProcessingScript_MouseEnter(object sender, EventArgs e)
        {
            showToolTip("Double click a filter to edit it. Select a filter and press Delete to remove it.");
        }

        #endregion

        #endregion

        #region tabPageEncoding

        private void textBoxNumbersOnly(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != '.')
            {
                e.Handled = true;
            }
        }

        #endregion

        #region tagPageAdvanced

        private void boxLevels_CheckedChanged(object sender, EventArgs e)
        {
            Filters.Levels = boxLevels.Checked ? new LevelsFilter() : null;
        }

        private void boxDeinterlace_CheckedChanged(object sender, EventArgs e)
        {
            Filters.Deinterlace = boxDeinterlace.Checked ? new DeinterlaceFilter() : null;
        }

        private void trackThreads_Scroll(object sender, EventArgs e)
        {
            labelThreads.Text = trackThreads.Value.ToString();
            UpdateArguments(sender, e);
        }

        private void trackSlices_Scroll(object sender, EventArgs e)
        {
            labelSlices.Text = GetSlices().ToString();
            UpdateArguments(sender, e);
        }

        #endregion

        #region Functions

        char[] invalidChars = Path.GetInvalidPathChars();

        private void SetFile(string path)
        {
            progressBarIndexing.Style = ProgressBarStyle.Marquee;
            progressBarIndexing.Value = 30;
            panelHideTheOptions.BringToFront();

            buttonGo.Enabled = false;
            buttonPreview.Enabled = false;
            buttonBrowseIn.Enabled = false;
            textBoxIn.Enabled = false;

            textBoxIn.Text = path;
            string fullPath = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            if (boxMetadataTitle.Text == _autoTitle || boxMetadataTitle.Text == "")
                boxMetadataTitle.Text = _autoTitle = name;
            if (textBoxOut.Text == _autoOutput || textBoxOut.Text == "")
                textBoxOut.Text = _autoOutput = Path.Combine(fullPath, name + ".webm");
            Program.InputFile = path;
            Program.FileMd5 = null;

            // Reset filters
            Filters.ResetFilters();
            listViewProcessingScript.Clear();
            toolStripButtonAdvancedScripting.Checked = false; // STUB: this part is weak
            toolStripButtonAdvancedScripting.Enabled = true;
            textBoxProcessingScript.Hide();
            listViewProcessingScript.Show();
            GenerateAvisynthScript();

            // Hash some of the file to make sure we didn't index it already
            labelIndexingProgress.Text = "Hashing...";
            using (MD5 md5 = MD5.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                var filename = new UTF8Encoding().GetBytes(name);
                var buffer = new byte[4096];

                filename.CopyTo(buffer, 0);
                stream.Read(buffer, filename.Length, 4096 - filename.Length);

                Program.FileMd5 = BitConverter.ToString(md5.ComputeHash(buffer));
                _indexFile = Path.Combine(Path.GetTempPath(), Program.FileMd5 + ".ffindex");
            }

            FFMSSharp.Index index = null;
            indexbw = new BackgroundWorker();
            BackgroundWorker extractbw = new BackgroundWorker();

            indexbw.WorkerSupportsCancellation = true;
            indexbw.WorkerReportsProgress = true;
            indexbw.ProgressChanged += new ProgressChangedEventHandler(delegate(object sender, ProgressChangedEventArgs e)
            {
                this.progressBarIndexing.Value = e.ProgressPercentage;
            });
            indexbw.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                FFMSSharp.Indexer indexer = new FFMSSharp.Indexer(path);

                indexer.UpdateIndexProgress += delegate(object sendertwo, FFMSSharp.IndexingProgressChangeEventArgs etwo)
                {
                    indexbw.ReportProgress((int)(((double)etwo.Current / (double)etwo.Total) * 100));
                    indexer.CancelIndexing = indexbw.CancellationPending;
                };

                try
                {
                    index = indexer.Index();
                }
                catch (OperationCanceledException)
                {
                    e.Cancel = true;
                    return;
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message);
                    e.Cancel = true;
                    return;
                }

                index.WriteIndex(_indexFile);
            };
            extractbw.DoWork += new DoWorkEventHandler(delegate
            {
                Program.AttachmentDirectory = Path.Combine(Path.GetTempPath(), Program.FileMd5 + ".attachments");
                Directory.CreateDirectory(Program.AttachmentDirectory);

                Program.SubtitleTracks = new List<int>();
                for (int i = 0; i <= index.NumberOfTracks; i++)
                {
                    if (index.GetTrack(i).TrackType == FFMSSharp.TrackType.Subtitle)
                    {
                        Program.SubtitleTracks.Add(i);
                        using (var ffmpeg = new FFmpeg(string.Format("-i \"{0}\" -map 0:{1} {2} -y", Program.InputFile, i, Path.Combine(Program.AttachmentDirectory, string.Format("sub{0}.ass", i)))))
                        {
                            ffmpeg.Start();
                            ffmpeg.WaitForExit();
                        }
                    }
                }

                using (var ffmpeg = new FFmpeg(string.Format("-dump_attachment:t \"\" -y -i \"{0}\"", Program.InputFile)))
                {
                    ffmpeg.StartInfo.WorkingDirectory = Program.AttachmentDirectory;
                    ffmpeg.Start();
                    ffmpeg.WaitForExit();
                }

                List<int> videoTracks = new List<int>(), audioTracks = new List<int>();
                for (int i = 0; i < index.NumberOfTracks; i++)
                {
                    switch(index.GetTrack(i).TrackType)
                    {
                        case FFMSSharp.TrackType.Video:
                            videoTracks.Add(i);
                            break;
                        case FFMSSharp.TrackType.Audio:
                            audioTracks.Add(i);
                            break;
                    }
                }

                if (videoTracks.Count == 0)
                    throw new Exception("No video tracks found!");

                else if (videoTracks.Count == 1)
                {
                    videotrack = videoTracks[0];
                }
                else
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        var dialog = new TrackSelectDialog("Video", videoTracks);
                        dialog.ShowDialog(this);
                        videotrack = dialog.SelectedTrack;
                    });
                }

                if (audioTracks.Count == 0)
                {
                    audioDisabled = true;
                }
                else if (audioTracks.Count == 1)
                {
                    audiotrack = audioTracks[0];
                    audioDisabled = false;
                }
                else
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        var dialog = new TrackSelectDialog("Audio", audioTracks);
                        dialog.ShowDialog(this);
                        audiotrack = dialog.SelectedTrack;
                    });
                    audioDisabled = false;
                }

                Program.VideoSource = index.VideoSource(path, videotrack);
                var frame = Program.VideoSource.GetFrame(0); // We're assuming that the entire video has the same settings here, which should be fine. (These options usually don't vary, I hope.)
                Program.VideoColorRange = frame.ColorRange;
                Program.VideoInterlaced = frame.InterlacedFrame;
                SetSlices(frame.EncodedResolution);
            });
            indexbw.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                indexing = false;
                buttonGo.Enabled = false;
                buttonGo.Text = "Convert";

                if (e.Cancelled)
                {
                    CancelIndexing();
                }
                else
                {
                    labelIndexingProgress.Text = "Extracting subtitle tracks and attachments...";
                    progressBarIndexing.Value = 30;
                    progressBarIndexing.Style = ProgressBarStyle.Marquee;

                    extractbw.RunWorkerAsync();
                }
            };
            extractbw.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    CancelIndexing();
                    const string text = "We couldn't find any video tracks!\nPlease use another input file.";
                    const string caption = "ERROR";
                    MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                if (audioDisabled)
                {
                    const string text = "We couldn't find any audio tracks.\nIf you want sound, please use another input file.\nIf you don't want audio in your output webm, there's nothing to worry about.";
                    const string caption = "FYI";
                    MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                buttonGo.Enabled = true;
                buttonPreview.Enabled = true;
                buttonBrowseIn.Enabled = true;
                textBoxIn.Enabled = true;
                toolStripFilterButtonsEnabled(true);

                if (Program.VideoColorRange == FFMSSharp.ColorRange.MPEG)
                    //boxLevels.Checked = true;
                if (Program.VideoInterlaced)
                    boxDeinterlace.Checked = true;

                panelHideTheOptions.SendToBack();
            };

            if (File.Exists(_indexFile))
            {
                index = new FFMSSharp.Index(_indexFile);
                if (index.BelongsToFile(path))
                {
                    labelIndexingProgress.Text = "Extracting subtitle tracks and attachments...";
                    progressBarIndexing.Value = 30;
                    progressBarIndexing.Style = ProgressBarStyle.Marquee;

                    extractbw.RunWorkerAsync();
                    return;
                }
            }

            indexing = true;
            buttonGo.Enabled = true;
            buttonGo.Text = "Cancel";
            progressBarIndexing.Style = ProgressBarStyle.Continuous;
            labelIndexingProgress.Text = "Indexing...";
            indexbw.RunWorkerAsync();
        }

        private void CancelIndexing()
        {
            textBoxIn.Text = "";
            textBoxOut.Text = "";
            Program.InputFile = null;
            Program.FileMd5 = null;
            buttonBrowseIn.Enabled = true;
            textBoxIn.Enabled = true;
            panelHideTheOptions.SendToBack();
        }

        private void ValidateInputFile(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new Exception("No input file!");

            if (invalidChars.Any(input.Contains))
                throw new Exception("Input path contains invalid characters!\nInvalid characters: " + string.Join(" ", invalidChars));

            if (!File.Exists(input))
                throw new Exception("Input file doesn't exist!");

            if (input.Any(c => c > 255)) // http://stackoverflow.com/questions/4459571/
                throw new Exception("Sorry, no moonrunes in your input file name.");
        }

        private void ValidateOutputFile(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                throw new Exception("No output file!");

            if (invalidChars.Any(output.Contains))
                throw new Exception("Output path contains invalid characters!\nInvalid characters: " + string.Join(" ", invalidChars));
        }

        private void UpdateArguments(object sender, EventArgs e)
        {
            try
            {
                string arguments = GenerateArguments();
                if (arguments != _autoArguments || _argumentError)
                {
                    textBoxArguments.Text = _autoArguments = arguments;
                    showToolTip(arguments, 5000);
                    _argumentError = false;
                }
            }
            catch (ArgumentException argExc)
            {
                textBoxArguments.Text = "ERROR: " + argExc.Message;
                showToolTip("ERROR: " + argExc.Message, 5000);
                _argumentError = true;
            }
        }

        private void WriteAvisynthScript(string avsFileName, string avsInputFile)
        {
            using (StreamWriter avscript = new StreamWriter(avsFileName, false))
            {
                avscript.WriteLine(string.Format("PluginPath = \"{0}\\\"", Path.Combine(Environment.CurrentDirectory, "Binaries", "Win32")));
                avscript.WriteLine("LoadPlugin(PluginPath+\"ffms2.dll\")");
                avscript.WriteLine("LoadCPlugin(PluginPath+\"assrender.dll\")");

                if (boxAudio.Checked)
                    avscript.WriteLine(string.Format("AudioDub(FFVideoSource(\"{0}\",cachefile=\"{1}\",track={2}), FFAudioSource(\"{0}\",cachefile=\"{1}\",track={3}))", avsInputFile, _indexFile, videotrack, audiotrack));
                else
                    avscript.WriteLine(string.Format("FFVideoSource(\"{0}\",cachefile=\"{1}\",track={2})", avsInputFile, _indexFile, videotrack));

                if (Filters.Deinterlace != null)
                {
                    avscript.WriteLine("LoadPlugin(PluginPath+\"TDeint.dll\")");
                    avscript.WriteLine(Filters.Deinterlace.ToString());
                }

                if (Filters.Levels != null)
                    avscript.WriteLine(Filters.Levels.ToString());

                avscript.Write(textBoxProcessingScript.Text);
            }
        }

        private void Preview()
        {
            string input = textBoxIn.Text;
            buttonPreview.Enabled = false;

            ValidateInputFile(input);

            // Generate the script if we're in simple mode
            if (Filters.Caption != null)
                Filters.Caption.BeforeEncode(Program.VideoSource.GetFrame(0).EncodedResolution);
            if (!toolStripButtonAdvancedScripting.Checked)
                GenerateAvisynthScript();

            // Make our temporary file for the AviSynth script
            string avsFileName = Path.GetTempFileName();
            WriteAvisynthScript(avsFileName, input);

            // Run ffplay
            var ffplay = new FFplay(string.Format("-window_title Preview -loop 0 -f avisynth \"{0}\"", avsFileName));
            ffplay.Exited += delegate
            {
                toolStripProcessingScript.Invoke(new Action(() => buttonPreview.Enabled = true));
            };
            ffplay.Start();
        }

        private void Convert()
        {
            string input = textBoxIn.Text;
            string output = textBoxOut.Text;

            if (input == output)
                throw new Exception("Input and output files are the same!");

            string options = textBoxArguments.Text;

            ValidateInputFile(input);
            ValidateOutputFile(output);

            if (options.Trim() == "" || _argumentError)
                options = GenerateArguments();

            // Generate the script if we're in simple mode
            if (Filters.Caption != null)
                Filters.Caption.BeforeEncode(GetResolution());
            if (!toolStripButtonAdvancedScripting.Checked)
                GenerateAvisynthScript();

            // Make our temporary file for the AviSynth script
            string avsFileName = Path.GetTempFileName();
            WriteAvisynthScript(avsFileName, input);

            string[] arguments;
            if (!boxHQ.Checked)
                arguments = new[] { string.Format(_template, output, options, "", "") };
            else
            {
                arguments = new string[2];
                arguments[0] = string.Format(_template, "NUL", options, "-pass 1"); // Windows
                arguments[1] = string.Format(_template, output, options, "-pass 2");

                if (!arguments[0].Contains("-an")) // skip audio encoding on the first pass
                    arguments[0] = arguments[0].Replace("-c:v libvpx", "-an -c:v libvpx"); // ugly as hell
            }

            new ConverterForm(this, avsFileName, arguments).ShowDialog(this);
        }

        private string GenerateArguments()
        {
            float limit = 0;
            string limitTo = "";
            if (!string.IsNullOrWhiteSpace(boxLimit.Text))
            {
                if (!float.TryParse(boxLimit.Text, out limit))
                    throw new ArgumentException("Invalid size limit!");
                limitTo = string.Format(" -fs {0}M", limit.ToString(CultureInfo.InvariantCulture)); //Should turn comma into dot
            }

            int bitrate = 900;
            if (!string.IsNullOrWhiteSpace(boxBitrate.Text))
            {
                if (!int.TryParse(boxBitrate.Text, out bitrate))
                    throw new ArgumentException("Invalid bitrate!");
            }
            else if (limit != 0)
            {
                double duration = GetDuration();

                if (duration > 0)
                    bitrate = (int)(8192 * limit / duration);
            }

            int threads = trackThreads.Value;
            int slices = GetSlices();

            string metadataTitle = "";
            if (!string.IsNullOrWhiteSpace(boxMetadataTitle.Text))
                metadataTitle = string.Format(" -metadata title=\"{0}\"", boxMetadataTitle.Text.Replace("\"", "\\\""));

            string HQ = "";
            if (boxHQ.Checked)
                HQ = " -quality best -lag-in-frames 16 -auto-alt-ref 1";

            string audioEnabled = boxAudio.Checked ? "" : "-an"; //-an if no audio
            return string.Format(_templateArguments, audioEnabled, bitrate, threads, slices, limitTo, metadataTitle, HQ);
        }

        /// <summary>
        /// Attempt to calculate the duration of the Avisynth script.
        /// </summary>
        /// <returns>The duration or -1 if automatic detection was unsuccessful.</returns>
        private double GetDuration()
        {
            if (toolStripButtonAdvancedScripting.Checked)
            {
                // The dirty way.

                using (XmlReader reader = XmlReader.Create(new StringReader(avsScriptInfo)))
                {
                    reader.ReadToFollowing("stream");

                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.Name == "duration")
                        {
                            return double.Parse(reader.Value);
                        }
                    }
                }

                return -1;
            }
            else
            {
                // The easy way.

                if (Filters.Trim != null)
                    return Filters.Trim.GetDuration();
                if (Filters.MultipleTrim != null)
                    return Filters.MultipleTrim.GetDuration();

                return Program.FrameToTime(Program.VideoSource.NumberOfFrames - 1);
            }
        }

        public Size GetResolution()
        {
            if (toolStripButtonAdvancedScripting.Checked)
            {
                // The dirty way.

                int width = -1, height = -1;

                using (XmlReader reader = XmlReader.Create(new StringReader(avsScriptInfo)))
                {
                    reader.ReadToFollowing("stream");

                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.Name == "width")
                        {
                            width = int.Parse(reader.Value);
                        }
                        if (reader.Name == "height")
                        {
                            height = int.Parse(reader.Value);
                        }
                    }
                }

                return new Size(width, height);
            }
            else
            {
                if (Filters.Resize != null)
                {
                    return new Size(Filters.Resize.TargetWidth, Filters.Resize.TargetHeight);
                }

                var frame = Program.VideoSource.GetFrame((Filters.Trim == null) ? 0 : Filters.Trim.TrimStart); // the video may have different frame resolutions

                if (Filters.Crop != null)
                {
                    int width = frame.EncodedResolution.Width - Filters.Crop.Left + Filters.Crop.Right;
                    int height = frame.EncodedResolution.Height - Filters.Crop.Top + Filters.Crop.Bottom;

                    return new Size(width, height);
                }

                return frame.EncodedResolution;
            }
        }

        private void GenerateAvisynthScript()
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("# This is an AviSynth script. You may write advanced commands below, or just press the buttons above for smooth sailing.");
            if (Filters.Subtitle != null)
                script.AppendLine(Filters.Subtitle.ToString());
            if (Filters.Caption != null)
                script.AppendLine(Filters.Caption.ToString());
            if (Filters.Overlay != null)
                script.AppendLine(Filters.Overlay.ToString());
            if (Filters.Trim != null)
                script.AppendLine(Filters.Trim.ToString());
            if (Filters.MultipleTrim != null)
                script.AppendLine(Filters.MultipleTrim.ToString());
            if (Filters.Crop != null)
                script.AppendLine(Filters.Crop.ToString());
            if (Filters.Resize != null)
                script.AppendLine(Filters.Resize.ToString());
            if (Filters.Reverse != null)
                script.AppendLine(Filters.Reverse.ToString());

            textBoxProcessingScript.Text = script.ToString();
        }

        void ProbeScript()
        {
            // Make our temporary file for the AviSynth script
            string avsFileName = Path.GetTempFileName();
            WriteAvisynthScript(avsFileName, textBoxIn.Text);

            // ffprobe it
            var ffprobe = new FFprobe(avsFileName);
            avsScriptInfo = ffprobe.Probe();
        }

        int GetSlices()
        {
            return (int)Math.Pow(2, trackSlices.Value - 1);
        }

        void SetSlices()
        {
            SetSlices(GetResolution());
        }

        void SetSlices(Size resolution)
        {
            int slices;

            if (resolution.Width * resolution.Height >= 2073600) // 1080p (1920*1080)
            {
                slices = 4;
            }
            else if (resolution.Width * resolution.Height >= 921600) // 720p (1280*720)
            {
                slices = 3;
            }
            else if (resolution.Width * resolution.Height >= 307200) // 480p (640*480)
            {
                slices = 2;
            }
            else
            {
                slices = 1;
            }

            trackSlices.Value = slices;
        }

        #endregion

        #region Update check

        const string githubowner = "nixxquality";
        const string githubrepo = "WebMConverter";
        const string githubasset = "Converter.zip";

        void CheckUpdate()
        {
            var thisVersion = new Version(Application.ProductVersion);

            showToolTip("Checking for updates...", 2000);
            
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(string.Format("https://api.github.com/repos/{0}/{1}/releases", githubowner, githubrepo));
            request.UserAgent = "WebMConverter-UpdateCheck";
            var response = request.GetResponse();
            string json;
            using (var sr = new StreamReader(response.GetResponseStream()))
            {
                json = sr.ReadToEnd();
            }
            dynamic releases = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

            string latestTag = releases[0].tag_name;
            var latestVersion = new Version(latestTag.Substring(1));

            if (thisVersion.CompareTo(latestVersion) >= 0)
            {
                showToolTip("Up to date!", 1000);
                return;
            }
            else
            {
                bool asset_available = false;

                try
                {
                    foreach (dynamic asset in releases[0].assets)
                    {
                        if (asset.name == githubasset)
                        {
                            asset_available = true;
                        }
                    }

                    if (!asset_available)
                        throw new Exception("");
                }
                catch
                {
                    showToolTip(string.Format("You're not up to date! Please visit github.com/{0}/{1}/releases/", githubowner, githubrepo));
                    return;
                }

                const string text = "There is a new version of WebM for Retards!\nWould you like to download it now?";
                const string caption = "Update available";

                this.Invoke((MethodInvoker)delegate
                {
                    DialogResult result;

                    result = MessageBox.Show(this, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);

                    if (result == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start(string.Format("https://github.com/{0}/{1}/releases/download/{2}/{3}", githubowner, githubrepo, latestTag, githubasset));
                        Application.Exit();
                    }
                });
            }
        }

        #endregion
    }
}
