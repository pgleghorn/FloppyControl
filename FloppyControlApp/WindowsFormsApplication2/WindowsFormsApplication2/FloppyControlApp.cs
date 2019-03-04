﻿using System;
using System.IO;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Security;
using System.Collections.Generic;
using FloppyControlApp.MyClasses;
 
namespace FloppyControlApp
{
    /*
        This application produces bin files of captured floppy disk RDATA. The format is:
        The stream can start in any track and any sector within the track or on the gap.
        Markers determine where data is:
        
        0x01 Index pulse is received, at this point in the data the Index marker was found and the start of the track,
        which is valid for PC DOS but not for Amiga as it doesn't use/ignores the index. The index is indicated using 
        the index offset within the header.
        
        0x02 Track marker, the next byte contains the track number. 
        All other bytes are data ranging from 0x04 to 0xff being counter data with an interval of 50ns (currently, 
        depending on the Osc used on the PIC micro controller).
    */

    public partial class FloppyControl : Form
    {
        class badsectorkeyval
        {
            public string name { get; set; }
            public int id { get; set; }
            public int threadid { get; set; }
        }


        public class ComboboxItem
        {
            public string Text { get; set; }
            public int id { get; set; }

            public override string ToString()
            {
                return Text;
            }
        }

        class ZeroCrossingData
        {
            public int before;
            public int after;
            public int negpos; // 0= negative, 1 = positive going
            public int zcbeforeafter;
            public int index;
        }

        class SectorMapContextMenu
        {
            public int track { get; set; }
            public int sector { get; set; }
            public int duration { get; set; }
            public MFMData sectordata { get; set; }
            public int cmd { get; set; }
        }

        private FDDProcessing processing;
        private ControlFloppy controlfloppy;
        private connectsocketNIVisa2 scope = new connectsocketNIVisa2();
        private BinaryReader reader;
        private StringBuilder SectorInfo = new StringBuilder();
        private StringBuilder tbtxt = new StringBuilder();
        private BinaryWriter writer;
        private Point BadSectorTooltipPos;
        private StringBuilder tbreceived = new StringBuilder();
        private Graphset graphset;
        private Histogram ECHisto = new Histogram();
        private Histogram ScatterHisto = new Histogram();
        private ScatterPlot scatterplot;
        private static readonly object lockaddmarker = new object();
        //private static uint markerpositionscnt;
        private string subpath;
        private string path = "";
        private string selectedPortName;
        private string[] openfilespaths;
        private int disablecatchkey = 0;
        private int binfilecount = 0; // Keep saving each capture under a different filename as to keep all captured data
        private int capturetime = 0;
        private int capturing = 0;
        private int selectedBaudRate = 5000000;
        private int graphselect = 0;
        private int maxthreads = 2;
        private int byteinsector = 0;
        //private int stepspertrack = 8;
        private byte[] TempSector = new byte[550];
        private byte[][] graphwaveform = new byte[15][];
        private bool AddData = false;
        private bool openFilesDlgUsed = false;
        private bool scanactive = false;
        private bool stopupdatingGraph = false;
        private int[] mfmbyteenc = new int[256];
        private int indexrxbufprevious = 0;

        public FloppyControl()
        {
            int i;
            InitializeComponent();
            processing = new FDDProcessing();

            processing.GetProcSettingsCallback += GetProcSettingsCallback;
            processing.rtbSectorMap = rtbSectorMap;
            processing.tbreceived = tbreceived;
            processing.sectormap.SectorMapUpdateGUICallback += SectorMapUpdateGUICallback;
            processing.sectormap.rtbSectorMap = rtbSectorMap;

            controlfloppy = new ControlFloppy();
            controlfloppy.rxbuf = processing.rxbuf;
            controlfloppy.processing = processing;

            scatterplot = new ScatterPlot(processing, processing.sectordata2, 0, 0, ScatterPictureBox);
            scatterplot.tbreiceved = tbreceived;
            scatterplot.rxbuf = processing.rxbuf;
            scatterplot.UpdateEvent += updateAnScatterPlot;
            scatterplot.ShowGraph += ScatterPlotShowGraphCallback;
            scatterplot.EditScatterplot = EditScatterPlotcheckBox.Checked;
            processing.indexrxbuf = 0;

            graphset = new Graphset(GraphPictureBox, Color.Black);
            graphset.UpdateGUI += updateGraphCallback;
            graphset.GetControlValues += GraphsetGetControlValuesCallback;
            graphset.tbreceived = tbreceived;
            outputfilename.Text = (string)Properties.Settings.Default["BaseFileName"];
            DirectStepCheckBox.Checked = (bool)Properties.Settings.Default["DirectStep"];
            MicrostepsPerTrackUpDown.Value = (int)Properties.Settings.Default["MicroStepping"];
            subpath = @Properties.Settings.Default["PathToRecoveredDisks"].ToString();
            
            EditOptioncomboBox.SelectedIndex = 0;
            EditModecomboBox.SelectedIndex = 0;

            textBoxReceived.AppendText("PortName: " + selectedPortName + "\r\n");

            //comboBoxPort.SelectedItem = "COM9";
            updateSliderLabels();
            string test;
            
            byte[] testhash = new byte[32];
            byte[] testsector = new byte[512];

            for (i = 0; i < 512; i++)
            {
                testsector[i] = (byte)(i & 0xFF);
            }


            // Set the steps per track default to MicroStepping, so a full step is used.
            // Note that due to the tracks are separated by 1 track, two full steps are taken
            // To do this, this value is multiplied by 2. Due to this you can only use the
            // smallest step 1 multiplied by 2. To get to the first step you can use TRK00 offset
            // increase or decrease by one.


            timer1.Start();
            MainTabControl.SelectedTab = ProcessingTab;
            //MainTabControl.SelectedTab = AnalysisPage;
            BadSectorTooltip.Hide();
            timer5.Start();
            GUITimer.Start();
            BluetoRedByteCopyToolBtn.Tag = new int();
            BluetoRedByteCopyToolBtn.Tag = 0;
            

            //ScatterPictureBox.MouseWheel += ScatterPictureBox_MouseWheel;

            ECHisto.setPanel(AnHistogramPanel);
            ScatterHisto.setPanel(Histogrampanel1);
            ProcessingTab.Enabled = false;
            PeriodBeyond8uscomboBox.SelectedIndex = 0;

            ChangeDiskTypeComboBox.Items.AddRange(Enum.GetNames(typeof(DiskFormat)));
            ProcessingModeComboBox.Items.AddRange(Enum.GetNames(typeof(ProcessingType)));
           
            ProcessingModeComboBox.SelectedItem = ProcessingType.adaptive1.ToString();

            ScanComboBox.Items.AddRange(Enum.GetNames(typeof(ScanMode)));
            ScanComboBox.SelectedItem = ScanMode.AdaptiveRate.ToString();


            if (HDCheckBox.Checked)
            {
                ScatterHisto.hd = 1;
                processing.procsettings.hd = 1;
                //hddiv = 2;
            }
            else
            {
                ScatterHisto.hd = 0;
                processing.procsettings.hd = 0;
                //hddiv = 1;
            }

            ProcessStatusLabel.BackColor = Color.Transparent;
            //BSEditByteUpDown.Tag = 0;

            // build gradient for scatter plot

            //int p;

            // 3F 64 8D
            // 63 100 141

        }

        private void ScatterPlotShowGraphCallback()
        {
            int grphcnt = graphset.Graphs.Count;

            int i;


            if (MainTabControl.SelectedTab == ErrorCorrectionTab)
            {
                int offset = 0;
                for (i = 0; i < processing.sectordata2.Count; i++)
                {
                    if (processing.sectordata2[i].rxbufMarkerPositions > scatterplot.rxbufclickindex)
                    {
                        offset = scatterplot.rxbufclickindex - processing.sectordata2[i - 1].rxbufMarkerPositions;
                        break;
                    }
                }

                MFMData sd = processing.sectordata2[i - 1];

                var mfms = FDDProcessing.mfms[sd.threadid];
                int index = 0;
                for (i = 0; i < mfms.Length; i++)
                {
                    if (mfms[i + sd.MarkerPositions] == 1)
                    {
                        index++;
                    }
                    if (index == offset)
                    {
                        break;
                    }
                }

                int mfmindex = i;
                mfmindex /= 8;
                mfmindex *= 8;
                int offsetmfmindex = 0;
                switch ((int)processing.diskformat)
                {
                    case 0:
                        return;
                        break;
                    case 1: //AmigaDos
                        offsetmfmindex = 48;
                        break;
                    case 2://diskspare
                        offsetmfmindex = 24;
                        break;
                    case 3://pc2m
                        offsetmfmindex = 0;
                        break;
                    case 4://pcdd
                        offsetmfmindex = 0;
                        break;
                    case 5://pchd
                        offsetmfmindex = 0;
                        break;
                }


                MFMByteStartUpDown.Value = mfmindex + offsetmfmindex;
                ScatterMinTrackBar.Value = offset;
                ScatterMaxTrackBar.Value = offset + 14;
                updateECInterface();
            }
            else
            {
                if (grphcnt == 0)
                {
                    return;
                }
                for (i = 0; i < grphcnt; i++)
                {
                    graphset.Graphs[i].datalength = 1000;
                    graphset.Graphs[i].dataoffset = scatterplot.graphindex - 500;

                    if (graphset.Graphs[i].dataoffset < 0)
                        graphset.Graphs[i].dataoffset = 0;

                    graphset.Graphs[i].changed = true;
                    graphset.Graphs[i].density = 1;
                }

                graphset.UpdateGraphs();
                MainTabControl.SelectedTab = AnalysisTab2;
            }

        }

        private void SectorMapUpdateGUICallback()
        {
            RecoveredSectorsLabel.Text = processing.sectormap.recoveredsectorcount.ToString();
            RecoveredSectorsWithErrorsLabel.Text = processing.sectormap.RecoveredSectorWithErrorsCount.ToString();
            GoodHdrCntLabel.Text = processing.GoodSectorHeaderCount.ToString();
            MarkersLabel.Text = processing.sectordata2.Count.ToString();
            BadSectorsCntLabel.Text = processing.sectormap.badsectorcnt.ToString();
            statsLabel.Text = processing.sectormap.s1.ToString("0.00") + " " +
                                processing.sectormap.s2.ToString("0.00") + " " +
                                processing.sectormap.s3.ToString("0.00");

            if (this.Width == 1620 || this.Width == 1630 || this.Width == 1680)
            {
                this.Width = processing.sectormap.WindowWidth;
            }

            switch ((int)processing.diskformat)
            {
                case 0:
                    DiskTypeLabel.Text = "Unknown";
                    //processing.processing.sectorspertrack = 0;
                    break;
                case 1:
                    DiskTypeLabel.Text = "AmigaDOS";
                    //processing.sectorspertrack = 11;
                    break;
                case 2:
                    DiskTypeLabel.Text = "DiskSpare";
                    //processing.sectorspertrack = 12;
                    break;
                case 3:
                    DiskTypeLabel.Text = "PC/MSX DD";
                    //processing.sectorspertrack = 9;
                    break;
                case 4:
                    DiskTypeLabel.Text = "PC HD";
                    //processing.sectorspertrack = 18;
                    break;
                case 5:
                    DiskTypeLabel.Text = "PC 2M";
                    //processing.sectorspertrack = 11;
                    break;

                default:
                    DiskTypeLabel.Text = processing.diskformat.ToString();
                    break;
            }

        }
        private void ProcessingGUICallback()
        {

        }

        private void GetProcSettingsCallback()
        {
            ProcessingType procmode = ProcessingType.adaptive1;
            if (ProcessingModeComboBox.SelectedItem.ToString() != "")
                procmode = (ProcessingType)Enum.Parse(typeof(ProcessingType), ProcessingModeComboBox.SelectedItem.ToString(), true);
            processing.procsettings.processingtype = procmode;
            //if (NormalradioButton.Checked == true) processing.procsettings.processingtype = ProcessingType.normal;
            //else if (AdaptradioButton.Checked == true) processing.procsettings.processingtype = ProcessingType.adaptive;
            //else if (AufitRadioButton.Checked == true) processing.procsettings.processingtype = ProcessingType.aufit;
            processing.procsettings.NumberOfDups = (int)DupsUpDown.Value;
            processing.procsettings.pattern = PeriodBeyond8uscomboBox.SelectedIndex;
            //tbreceived.Append("Combobox:" + PeriodBeyond8uscomboBox.SelectedIndex + "\r\n");

            processing.procsettings.offset = OffsetvScrollBar1.Value;
            processing.procsettings.min = MinvScrollBar.Value + processing.procsettings.offset;
            processing.procsettings.four = FourvScrollBar.Value + processing.procsettings.offset;
            processing.procsettings.six = SixvScrollBar.Value + processing.procsettings.offset;
            processing.procsettings.max = EightvScrollBar.Value + processing.procsettings.offset;
            processing.procsettings.SkipPeriodData = false;
            processing.procsettings.AutoRefreshSectormap = AutoRefreshSectorMapCheck.Checked;
            processing.procsettings.start = (int)rxbufStartUpDown.Value;
            processing.procsettings.end = (int)rxbufEndUpDown.Value;

            processing.procsettings.finddupes = FindDupesCheckBox.Checked;
            processing.procsettings.rateofchange = (float)RateOfChangeUpDown.Value;
            //processing.procsettings.platform = platform; // 1 = Amiga
            processing.procsettings.UseErrorCorrection = ECOnRadio.Checked;
            processing.procsettings.OnlyBadSectors = OnlyBadSectorsRadio.Checked;
            processing.procsettings.AddNoise = AddNoisecheckBox.Checked;

            processing.procsettings.limittotrack = (int)LimitToTrackUpDown.Value;
            processing.procsettings.limittosector = (int)LimitToSectorUpDown.Value;
            processing.procsettings.NumberOfDups = (int)DupsUpDown.Value;
            processing.procsettings.LimitTSOn = LimitTSCheckBox.Checked;
            processing.procsettings.IgnoreHeaderError = IgnoreHeaderErrorCheckBox.Checked;
            //processing.procsettings.AdaptOffset = (int)AdaptOffsetUpDown.Value;
            processing.procsettings.AdaptOffset2 = (float)AdaptOfsset2UpDown.Value;
            processing.procsettings.rateofchange2 = (int)RateOfChange2UpDown.Value;

            if (LimitToScttrViewcheckBox.Checked == true && OnlyBadSectorsRadio.Checked == true)
            {
                processing.procsettings.addnoiselimitstart = ScatterMinTrackBar.Value + 50;
                processing.procsettings.addnoiselimitend = ScatterMaxTrackBar.Value + 50;
            }
            else
            {
                processing.procsettings.addnoiselimitstart = 0;
                processing.procsettings.addnoiselimitend = processing.indexrxbuf;
            }
            processing.procsettings.addnoiserndamount = (int)RndAmountUpDown.Value;
        }

        private void ConvertToMFMBtn_Click(object sender, EventArgs e)
        {
            int i;
            StringBuilder tbt = new StringBuilder();
            StringBuilder txt = new StringBuilder();

            // Convert string of hex encoded ascii to byte array
            byte[] bytes = FDDProcessing.HexToBytes(tbBIN.Text);

            if (ANPCRadio.Checked)
            {
                // Convert byte array to MFM
                byte[] mfmbytes = processing.BIN2MFMbits(ref bytes, bytes.Count(), 0, false);
                byte[] bytebuf = new byte[tbBIN.Text.Length];

                // Convert mfm to string
                tbMFM.Text = Encoding.ASCII.GetString(processing.BIN2MFMbits(ref bytes, bytes.Count(), 0, true));

                for (i = 0; i < mfmbytes.Length / 16; i++)
                {
                    bytebuf[i] = processing.MFMBits2BINbyte(ref mfmbytes, (i * 16));
                    tbt.Append(bytebuf[i].ToString("X2") + " ");
                    if (bytebuf[i] > ' ' && bytebuf[i] < 127) txt.Append((char)bytebuf[i]);
                    else txt.Append(".");

                }
            }
            else
            if (ANAmigaRadio.Checked)
            {
                byte[] mfmbytes = new byte[bytes.Length * 8];
                int j;

                // Convert byte array to MFM
                for (i = 0; i < bytes.Length; i++)
                {
                    for (j = 0; j < 8; j++)
                    {
                        mfmbytes[i * 8 + j] = (byte)(bytes[i] >> (7 - j) & 1);
                    }
                }
                //byte[] mfmbytes = BIN2MFMbits(ref bytes, bytes.Count(), 0, false);
                byte[] bytebuf = new byte[tbBIN.Text.Length];

                // Convert mfm to string
                tbMFM.Text = Encoding.ASCII.GetString(processing.BIN2MFMbits(ref bytes, bytes.Count(), 0, true));

                bytebuf = processing.amigamfmdecodebytes(mfmbytes, 0, mfmbytes.Length); // This doesn't convert sector properly yet

                // Convert mfm back to bytes
                for (i = 0; i < bytebuf.Length; i++)
                {
                    //bytebuf[i] = MFMBits2BINbyte(ref mfmbytes, (i * 16));
                    tbt.Append(bytebuf[i].ToString("X2") + " ");
                    if (bytebuf[i] > 31 && bytebuf[i] < 127) txt.Append((char)bytebuf[i]);
                    else txt.Append(".");
                    if (i % 16 == 15) tbt.Append("\r\n");
                    if (i % 32 == 31) txt.Append("\r\n");
                }
            }
            else
            if (AmigaMFMRadio.Checked)
            {
                byte[] mfmbytes;

                // Convert bytes to Amiga mfm
                mfmbytes = processing.amigamfmencodebytes(bytes, 0, bytes.Length);
                byte[] bytebuf = new byte[tbBIN.Text.Length];

                // Convert mfm to string
                tbMFM.Text = Encoding.ASCII.GetString(processing.BIN2MFMbits(ref bytes, bytes.Count(), 0, true));

                bytebuf = processing.amigamfmdecodebytes(mfmbytes, 0, mfmbytes.Length); // This doesn't convert sector properly yet

                // Convert mfm back to bytes
                for (i = 0; i < bytebuf.Length; i++)
                {
                    //bytebuf[i] = MFMBits2BINbyte(ref mfmbytes, (i * 16));
                    tbt.Append(bytebuf[i].ToString("X2") + " ");
                    if (bytebuf[i] > 31 && bytebuf[i] < 127) txt.Append((char)bytebuf[i]);
                    else txt.Append(".");
                    if (i % 16 == 15) tbt.Append("\r\n");
                    if (i % 32 == 31) txt.Append("\r\n");
                }
            }

            tbTest.Clear();
            AntxtBox.Clear();
            tbTest.AppendText(tbt.ToString());
            AntxtBox.AppendText(txt.ToString());
        }

        private void OpenBinFileButton_Click(object sender, EventArgs e)
        {
            AddData = false;

            openFileDialog1.Multiselect = true;
            openFileDialog1.Filter = "Bin files (*.bin)|*.bin|Kryoflux (*.raw)|*.raw|All files (*.*)|*.*";

            openFileDialog1.ShowDialog();
        }
        private void AddDataButton_Click(object sender, EventArgs e)
        {
            AddData = true;

            openFileDialog1.Multiselect = true;
            openFileDialog1.Filter = "Bin files (*.bin)|*.bin|Kryoflux (*.raw)|*.raw|All files (*.*)|*.*";

            openFileDialog1.ShowDialog();
        }

        private void openFileDialog1_FileOk(object sender, CancelEventArgs e)
        {
            if (outputfilename.Text != "Dump")
            {
                openFileDialog1.InitialDirectory = subpath + @"\" + outputfilename.Text;
            }

            openfilespaths = openFileDialog1.FileNames;
            openFilesDlgUsed = true;

        }

        private void openfiles()
        {
            int numberOfFiles, loaderror = 0;
            //byte a;
            byte[] temp = new byte[1];
            byte[] histogram = new byte[256];
            string path1 = @"";

            List<byte[]> tempbuf = new List<byte[]>();

            //markerpositionscnt = 0;
            
            openFilesDlgUsed = false;
            numberOfFiles = openfilespaths.Length;

            if (AddData == false) // If open is clicked, replace the current data
            {
                resetinput();
                //TrackPosInrxdatacount = 0;
                processing.indexrxbuf = 0;

                // Clear all sectordata
                // if ( processing.sectordata2 != null)
                //     for (i = 0; i < processing.sectordata2.Count; i++)
                //
                //MFMData sectordata;
                if (processing.sectordata2 != null)
                {
                    processing.sectordata2.Clear();
                    //sectordata2.TryTake(out sectordata);
                }
            }

            //New!!!
            textBoxFilesLoaded.Text += "\r\n"; // Add a white line to indicate what groups of files are loaded
            processing.CurrentFiles = "";
            // Write period data to disk in bin format
            if (AddData == true)  // If Add data is clicked, the data is appended to the rxbuf array
            {
                tempbuf.Add( processing.rxbuf.SubArray(0,processing.indexrxbuf) );
            }
            String ext;
            foreach (String file in openfilespaths)
            {
                try
                {
                    reader = new BinaryReader(new FileStream(file, FileMode.Open));

                    path1 = Path.GetFileName(file);
                    ext = Path.GetExtension(file);
                    textBoxFilesLoaded.Text += path1 + "\r\n";
                    processing.CurrentFiles += path1 + "\r\n";
                    var indexof = path1.IndexOf("_");
                    if (indexof != -1)
                    {
                        outputfilename.Text = path1.Substring(0, path1.IndexOf("_"));
                        Properties.Settings.Default["BaseFileName"] = outputfilename.Text;
                    }

                    Properties.Settings.Default.Save();
                    //reader.BaseStream.Length
                    tbSectorMap.Text += openFileDialog1.FileName + "\r\n";
                    tbSectorMap.Text += Path.GetFileName(openFileDialog1.FileName) + "\r\n";
                    tbSectorMap.Text += "FileLength: " + reader.BaseStream.Length + "\r\n";
                    
                    if( ext ==".raw")
                    {
                        //filter kryoflux meta data
                        var tempdat = reader.ReadBytes((int)reader.BaseStream.Length);
                        byte[] tempdat2 = new byte[tempdat.Length];

                        int cnt = 0;

                        for ( int i=0; i<tempdat.Length-4; i++)
                        {
                            if( tempdat[i] == 0x0d && tempdat[i+1] == 0x02 && tempdat[i+2] == 0x0c && tempdat[i+3] == 0x00)
                            {
                                i += 16;
                            }
                            tempdat2[cnt++] = tempdat[i];
                        }

                        tempbuf.Add(tempdat2);
                    }
                    else tempbuf.Add( reader.ReadBytes((int)reader.BaseStream.Length)) ;
                }
                catch (SecurityException ex)
                {
                    // The user lacks appropriate permissions to read files, discover paths, etc.
                    MessageBox.Show("Security error. Please contact your administrator for details.\n\n" +
                        "Error message: " + ex.Message + "\n\n" +
                        "Details (send to Support):\n\n" + ex.StackTrace
                    );
                    loaderror = 1;
                }
                catch (Exception ex)
                {
                    // Could not load the image - probably related to Windows file system permissions.
                    MessageBox.Show("Cannot display the image: " + file.Substring(file.LastIndexOf('\\'))
                        + ". You may not have permission to read the file, or " +
                        "it may be corrupt.\n\nReported error: " + ex.Message);
                    loaderror = 1;
                }
                if (loaderror != 1)
                {
                    //temp.CopyTo(processing.rxbuf, processing.indexrxbuf);
                    processing.indexrxbuf += (int)reader.BaseStream.Length;
                }
                if (reader != null)
                {
                    //reader.Flush();
                    reader.Close();
                    reader.Dispose();
                }

                //Application.DoEvents();
            }

            byte[] extra = new byte[processing.indexrxbuf];
            tempbuf.Add(extra);
            processing.rxbuf = tempbuf.SelectMany(a => a).ToArray();
            Setrxbufcontrol();

            if ( processing.indexrxbuf < 100000)
                scatterplot.AnScatViewlength = processing.indexrxbuf;
            else scatterplot.AnScatViewlength = 99999;
            scatterplot.AnScatViewoffset = 0;
            scatterplot.UpdateScatterPlot();
            updateHistoAndSliders();
            //ScatterHisto.DoHistogram(rxbuf, (int)rxbufStartUpDown.Value, (int)rxbufEndUpDown.Value);
            if (processing.indexrxbuf > 0)
                ProcessingTab.Enabled = true;
            //createhistogram1();
        }

        private void FloppyControl_KeyDown(object sender, KeyEventArgs e)
        {
            processing.stop = 0;
            if (disablecatchkey == 0)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    scope.Disconnect();
                    this.Close();
                    return;
                }
                if (e.KeyCode == Keys.A)
                {
                    RateOfChange2UpDown.Focus();
                    Application.DoEvents();
                    processing.StartProcessing(1);
                }
                if (e.KeyCode == Keys.P)
                    ProcessPC();
                if (e.KeyCode == Keys.S)
                    ScanButton.PerformClick();

                if (MainTabControl.SelectedTab == AnalysisTab2)
                {
                    if (e.KeyCode == Keys.D1)
                        EditOptioncomboBox.SelectedIndex = 0;
                    if (e.KeyCode == Keys.D2)
                        EditOptioncomboBox.SelectedIndex = 1;
                    if (e.KeyCode == Keys.D3)
                        EditOptioncomboBox.SelectedIndex = 2;

                    if (e.Modifiers == Keys.Control && e.KeyCode == Keys.Z)
                        EditUndobutton.PerformClick();
                }

            }
        }

        // Do the Amiga sector data processing
        private void Process2Btn_Click(object sender, EventArgs e)
        {
            processing.stop = 0;
            ProcessAmiga();
        }
        private void ProcessAmiga()
        {
            if (ClearDatacheckBox.Checked)
                resetprocesseddata();
            //textBoxReceived.Clear();
            processing.scatterplotstart = scatterplot.AnScatViewlargeoffset + scatterplot.AnScatViewoffset;
            processing.scatterplotend = scatterplot.AnScatViewlargeoffset + scatterplot.AnScatViewoffset + scatterplot.AnScatViewlength;
            processing.StartProcessing(1);
        }

        private void ProcessPC()
        {
            if (ClearDatacheckBox.Checked)
                resetprocesseddata();
            //textBoxReceived.Clear();
            processing.scatterplotstart = scatterplot.AnScatViewlargeoffset + scatterplot.AnScatViewoffset;
            processing.scatterplotend = scatterplot.AnScatViewlargeoffset + scatterplot.AnScatViewoffset + scatterplot.AnScatViewlength;
            processing.StartProcessing(0);
            ChangeDiskTypeComboBox.SelectedItem = processing.diskformat.ToString();
        }

        private void ProcessPCBtn_Click(object sender, EventArgs e)
        {
            processing.stop = 0;
            ProcessPC();
        }
        
        public void updateForm()
        {
            //RecoveredSectorsLabel.Text = recoveredsectorcount.ToString();

            //RecoveredSectorsLabel.Invalidate();
            //RecoveredSectorsLabel.Update();
            //RecoveredSectorsLabel.Refresh();
            Application.DoEvents();
        }

        // Updates the labels under the sliders
        // as well as the indicators under the histogram
        private void updateSliderLabels()
        {
            int x, y;

            if ((OffsetvScrollBar1.Value + MinvScrollBar.Value) < 0)
                MinLabel.Text = 0.ToString("X2");
            else MinLabel.Text = (OffsetvScrollBar1.Value + MinvScrollBar.Value).ToString("X2");
            FourLabel.Text = (OffsetvScrollBar1.Value + FourvScrollBar.Value).ToString("X2");
            SixLabel.Text = (OffsetvScrollBar1.Value + SixvScrollBar.Value).ToString("X2");
            EightLabel.Text = (OffsetvScrollBar1.Value + EightvScrollBar.Value).ToString("X2");
            Offsetlabel.Text = OffsetvScrollBar1.Value.ToString("D2");

            System.Drawing.Graphics formGraphics = Histogrampanel1.CreateGraphics();

            using (var bmp = new System.Drawing.Bitmap(580, 12))
            {
                LockBitmap lockBitmap = new LockBitmap(bmp);
                lockBitmap.LockBits();
                lockBitmap.FillBitmap(SystemColors.Control);

                x = MinvScrollBar.Value - 4 + OffsetvScrollBar1.Value;
                y = 0;
                lockBitmap.Line(000 + x, 005 + y, 005 + x, 000 + y, Color.Black);
                lockBitmap.Line(005 + x, 000 + y, 010 + x, 005 + y, Color.Black);
                lockBitmap.Line(010 + x, 005 + y, 000 + x, 005 + y, Color.Black);

                x = FourvScrollBar.Value - 4 + OffsetvScrollBar1.Value;

                lockBitmap.Line(000 + x, 005 + y, 005 + x, 000 + y, Color.Black);
                lockBitmap.Line(005 + x, 000 + y, 010 + x, 005 + y, Color.Black);
                lockBitmap.Line(010 + x, 005 + y, 000 + x, 005 + y, Color.Black);

                x = SixvScrollBar.Value - 4 + OffsetvScrollBar1.Value;

                lockBitmap.Line(000 + x, 005 + y, 005 + x, 000 + y, Color.Black);
                lockBitmap.Line(005 + x, 000 + y, 010 + x, 005 + y, Color.Black);
                lockBitmap.Line(010 + x, 005 + y, 000 + x, 005 + y, Color.Black);

                x = EightvScrollBar.Value - 4 + OffsetvScrollBar1.Value;

                lockBitmap.Line(000 + x, 005 + y, 005 + x, 000 + y, Color.Black);
                lockBitmap.Line(005 + x, 000 + y, 010 + x, 005 + y, Color.Black);
                lockBitmap.Line(010 + x, 005 + y, 000 + x, 005 + y, Color.Black);

                lockBitmap.UnlockBits();
                formGraphics.DrawImage(bmp, 0, 103);
            }

            formGraphics.Dispose();
        }

        

        private void ConnectClassbutton_Click(object sender, EventArgs e)
        {
            controlfloppy.binfilecount = binfilecount;
            controlfloppy.DirectStep = DirectStepCheckBox.Checked;
            controlfloppy.MicrostepsPerTrack = (int)MicrostepsPerTrackUpDown.Value;
            controlfloppy.trk00offset = (int)TRK00OffsetUpDown.Value;
            controlfloppy.EndTrack = (int)EndTracksUpDown.Value;
            controlfloppy.StartTrack = (int)StartTrackUpDown.Value;
            controlfloppy.tbr = tbreceived;
            //processing.indexrxbuf            = indexrxbuf;
            controlfloppy.StepStickMicrostepping = (int)Properties.Settings.Default["MicroStepping"];
            controlfloppy.outputfilename = outputfilename.Text;
            controlfloppy.rxbuf = processing.rxbuf;

            // Callbacks
            controlfloppy.updateHistoAndSliders = updateHistoAndSliders;
            controlfloppy.ControlFloppyScatterplotCallback = ControlFloppyScatterplotCallback;
            controlfloppy.Setrxbufcontrol = Setrxbufcontrol;

            if (!controlfloppy.serialPort1.IsOpen) // Open connection if it's closed
            {
                controlfloppy.ConnectFDD();
                if (controlfloppy.serialPort1.IsOpen)
                {
                    LabelStatus.Text = "Connected.";
                    ConnectClassbutton.Text = "Connected";
                    ConnectClassbutton.BackColor = Color.FromArgb(0xD0, 0xF0, 0xD0);
                }
                else
                {
                    LabelStatus.Text = "Disconnected.";
                    ConnectClassbutton.Text = "Connect";
                    ConnectClassbutton.BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
                }
            }
            else // Close connection if open
            {
                controlfloppy.Disconnect();
                LabelStatus.Text = "Disconnected.";
                ConnectClassbutton.Text = "Connect";
                ConnectClassbutton.BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
            }
        }
        // Update scatterplot while capturing
        public void ControlFloppyScatterplotCallback()
        {
            
            scatterplot.rxbuf = processing.rxbuf;
            scatterplot.AnScatViewlargeoffset = processing.rxbuf.Length - controlfloppy.recentreadbuflength;
            if (scatterplot.AnScatViewlargeoffset < 0)
                scatterplot.AnScatViewlargeoffset = 0;
            scatterplot.AnScatViewoffset = 0;
            scatterplot.AnScatViewlength = controlfloppy.recentreadbuflength;
            controlfloppy.recentreadbuflength = 0;
            //scatterplot.UpdateScatterPlot();
            CurrentTrackLabel.Text = controlfloppy.currenttrackPrintable.ToString();
            updateAllGraphs();
            
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (capturing == 1 || processing.processing == 1)
                capturetime++;
            // bytes per second
            // and total bytes received

            if (controlfloppy.capturecommand == 1)
            {
                BytesReceivedLabel.Text = string.Format("{0:n0}", processing.indexrxbuf);
                BytesPerSecondLabel.Text = string.Format("{0:n0}", controlfloppy.bytespersecond);
                CaptureTimeLabel.Text = capturetime.ToString();
                controlfloppy.bytespersecond = 0;
                BufferSizeLabel.Text = string.Format("{0:n0}", processing.indexrxbuf);

                indexrxbufprevious = processing.rxbuf.Length;
                //processing.rxbuf = controlfloppy.tempbuffer.Skip(Math.Max(0, controlfloppy.tempbuffer.Count()-30)).SelectMany(a => a).ToArray();
                

                //tbreceived.Append("indexrxbufprevious: "+indexrxbufprevious.ToString()+"processing.rxbuf.Length:"+ processing.rxbuf.Length.ToString());
                controlfloppy.rxbuf = processing.rxbuf;
                if (processing.rxbuf.Length > 100000)
                    controlfloppy.recentreadbuflength = 100000; // controlfloppy.recentreadbuflength = processing.indexrxbuf - indexrxbufprevious;
                tbreceived.Append("Recent received:"+controlfloppy.recentreadbuflength.ToString());
                processing.indexrxbuf = processing.rxbuf.Length - 1;
                ControlFloppyScatterplotCallback();
            }


            if (processing.indexrxbuf > 0)
                ProcessingTab.Enabled = true;



            if (openFilesDlgUsed == true)
            {
                openfiles();
            }
        }

        private void Histogrampanel1_Paint(object sender, PaintEventArgs e)
        {
            if (processing.indexrxbuf > 0)
            {
                ScatterHisto.DoHistogram();
                updateSliderLabels();
                updateAnScatterPlot();
            }
        }

        private void FourvScrollBar_ValueChanged(object sender, EventArgs e)
        {
            if (!scanactive)
            {
                updateSliderLabels();
                scatterplot.UpdateScatterPlot();
                scatterplot.UpdateScatterPlot();
            }
        }

        private void SaveDiskImageButton_Click(object sender, EventArgs e)
        {
            int i, j, bytecount = 0;
            string extension = ".ADF";
            string diskformatstring = "";
            int disksize1 = 0;
            int ioerror = 0;

            //string fullpath = "";
            // Write period data to disk in bin format

            path = subpath + @"\" + outputfilename.Text + @"\";

            if (processing.diskformat == DiskFormat.amigados)
            {
                bytecount = processing.bytespersector * processing.sectorspertrack * 160;
                extension = ".ADF";
                diskformatstring = "ADOS";
            }
            else if (processing.diskformat == DiskFormat.diskspare)
            {
                bytecount = processing.bytespersector * processing.sectorspertrack * 160;
                extension = ".ADF";
                diskformatstring = "DiskSpare";
            }
            else if (processing.diskformat == DiskFormat.pcdd) // DD
            {
                bytecount = processing.bytespersector * processing.sectorspertrack * 80 * 2;
                extension = ".DSK";
                diskformatstring = "PCDOS DD";
            }
            else if (processing.diskformat == DiskFormat.pchd) // HD
            {
                bytecount = processing.bytespersector * processing.sectorspertrack * 80 * 2;
                extension = ".DSK";
                diskformatstring = "PCDOS HD";
            }
            else if (processing.diskformat == DiskFormat.pc2m) // 2M
            {
                bytecount = 512 * 18 + (1024 * 11 * 83 * 2); // First track is normal PC HD format
                extension = ".DSK";
                diskformatstring = "PCDOS 2M";
            }
            else if (processing.diskformat == DiskFormat.pc360kb525in) // 360KB 5.25"
            {
                bytecount = processing.bytespersector * processing.sectorspertrack * 40 * 2;
                extension = ".DSK";
                diskformatstring = "PCDOS 360KB";
            }

            bool exists = System.IO.Directory.Exists(path);

            if (!exists)
                System.IO.Directory.CreateDirectory(path);

            //Only save if there's any data to save
            if (bytecount != 0)
            {
                path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_sectormap_" + binfilecount.ToString("D3") + ".txt";
                while (File.Exists(path))
                {
                    binfilecount++;
                    path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_sectormap_" + binfilecount.ToString("D3") + ".txt";
                }

                rtbSectorMap.AppendText("Disk Format: " + diskformatstring + "\r\nRecovered sectors: " + processing.sectormap.recoveredsectorcount + "\r\n");
                rtbSectorMap.AppendText("Recovered sectors with error: " + processing.sectormap.RecoveredSectorWithErrorsCount + "\r\n");

                //fullpath = path + outputfilename + "_trackinfo.txt";
                try
                {
                    File.WriteAllText(path, rtbSectorMap.Text);
                }
                catch (IOException ex)
                {
                    textBoxReceived.AppendText("IO error writing sector map: \r\n" + ex.ToString());
                }

                if (processing.diskformat == DiskFormat.diskspare) // 960 KB to 984 KB diskspare
                {


                    for (j = 80; j <= 83; j++) // Multiple DiskSpare images, not sure what format I used, so try it in WinUAE
                    {
                        bytecount = j * processing.bytespersector * 12 * 2; //Also save with the two extra tracks 984KB
                        disksize1 = bytecount / 1024;
                        path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + disksize1 + "KB" + "_disk_" + binfilecount.ToString("D3") + extension;
                        while (File.Exists(path))
                        {
                            binfilecount++;
                            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + disksize1 + "KB" + "_disk_" + binfilecount.ToString("D3") + extension;
                        }

                        tbreceived.Append(path);
                        try
                        {
                            writer = new BinaryWriter(new FileStream(path, FileMode.Create));
                        }
                        catch (IOException ex)
                        {
                            textBoxReceived.AppendText("IO error writing DiskSpare image: \r\n" + ex.ToString());
                            ioerror = 1;
                        }
                        if (ioerror == 0) // skip writing on io error
                        {
                            for (i = 0; i < bytecount; i++) // writing uints
                                writer.Write((byte)processing.disk[i]);
                            if (writer != null)
                            {
                                writer.Flush();
                                writer.Close();
                                writer.Dispose();
                            }
                        }
                        else ioerror = 0;
                    }
                }

                if (processing.diskformat == DiskFormat.amigados) // ADOS 880 KB
                {
                    path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_disk_" + binfilecount.ToString("D3") + extension;
                    while (File.Exists(path))
                    {
                        binfilecount++;
                        path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_disk_" + binfilecount.ToString("D3") + extension;
                    }

                    bytecount = 80 * processing.bytespersector * 11 * 2; //Also save with the two extra tracks 984KB
                    disksize1 = bytecount / 1024;
                    try
                    {
                        writer = new BinaryWriter(new FileStream(path, FileMode.Create));
                    }
                    catch (IOException ex)
                    {
                        textBoxReceived.AppendText("IO error writing ADOS image: \r\n" + ex.ToString());
                        ioerror = 1;
                    }
                    if (ioerror == 0) // skip writing on io error
                    {
                        for (i = 0; i < bytecount; i++) // writing uints
                            writer.Write((byte)processing.disk[i]);
                        if (writer != null)
                        {
                            writer.Flush();
                            writer.Close();
                            writer.Dispose();
                        }
                    }
                    else ioerror = 0;
                }

                if (processing.diskformat == DiskFormat.pcdd || 
                    processing.diskformat == DiskFormat.pchd || 
                    processing.diskformat == DiskFormat.pc2m ||
                    processing.diskformat == DiskFormat.pc360kb525in) //PC 720 KB dd or 1440KB hd
                {
                    int SectorsPerDisk;
                    SectorsPerDisk = (processing.disk[20] << 8) | processing.disk[19];
                    tbreceived.Append("Sectors per disk: " + SectorsPerDisk + "\r\n");

                    if (SectorsPerDisk == 720 && processing.diskformat == DiskFormat.pcssdd)
                    {
                        path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_disk_" + binfilecount.ToString("D3") + extension;
                        while (File.Exists(path))
                        {
                            binfilecount++;
                            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_disk_" + binfilecount.ToString("D3") + extension;
                        }

                        try
                        {
                            writer = new BinaryWriter(new FileStream(path, FileMode.Create));
                        }
                        catch (IOException ex)
                        {
                            textBoxReceived.AppendText("IO error: " + ex.ToString());
                            ioerror = 1;
                        }
                        if (ioerror == 0) // skip writing on io error
                        {
                            int interleave = 0;
                            for (i = 0; i < bytecount; i++)
                            {

                                writer.Write((byte)processing.disk[i]);
                                interleave++;
                                if (interleave == 512 * 9)
                                {
                                    interleave = 0;
                                    i += 512 * 9;
                                }
                            }
                            if (writer != null)
                            {
                                writer.Flush();
                                writer.Close();
                                writer.Dispose();
                            }
                        }
                        else ioerror = 0;
                    }
                    else
                    {
                        path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_disk_" + binfilecount.ToString("D3") + extension;
                        while (File.Exists(path))
                        {
                            binfilecount++;
                            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_disk_" + binfilecount.ToString("D3") + extension;
                        }

                        try
                        {
                            writer = new BinaryWriter(new FileStream(path, FileMode.Create));
                        }
                        catch (IOException ex)
                        {
                            textBoxReceived.AppendText("IO error: " + ex.ToString());
                            ioerror = 1;
                        }
                        if (ioerror == 0) // skip writing on io error
                        {
                            for (i = 0; i < bytecount; i++)
                                writer.Write((byte)processing.disk[i]);
                            if (writer != null)
                            {
                                writer.Flush();
                                writer.Close();
                                writer.Dispose();
                            }
                        }
                        else ioerror = 0;
                    }
                }
                
            }
        }

        // Resets all data that was produced by processing but keeps rxbuf intact
        private void resetprocesseddata()
        {
            int i;
            FDDProcessing.badsectorhash = new byte[5000000][];

            BadSectorListBox.Items.Clear();
            processing.sectordata2.Clear();

            for (i = 0; i < FDDProcessing.mfmsindex; i++)
            {

                //BadSectors[i] = new byte[0];
                FDDProcessing.mfms[i] = new byte[0];
            }
            OnlyBadSectorsRadio.Checked = false; // When the input buffer is changed or empty, we can't scan for only bad sectors
            FDDProcessing.mfmsindex = 0;
            GC.Collect();
        }

        private void resetinput()
        {
            int i;
            ProcessingTab.Enabled = false;
            FDDProcessing.badsectorhash = new byte[5000000][];

            BadSectorListBox.Items.Clear();
            processing.sectordata2.Clear();

            for (i = 0; i < FDDProcessing.mfmsindex; i++)
            {

                //BadSectors[i] = new byte[0];
                FDDProcessing.mfms[i] = new byte[0];
            }
            OnlyBadSectorsRadio.Checked = false; // When the input buffer is changed or empty, we can't scan for only bad sectors
            ECOnRadio.Checked = true;
            StringBuilder t = new StringBuilder();
            //mfmlength = 0;
            Array.Clear(processing.rxbuf, 0, processing.rxbuf.Length);
            //TrackPosInrxdatacount = 0;
            processing.indexrxbuf = 0;
            FDDProcessing.mfmsindex = 0;

            rxbufStartUpDown.Maximum = processing.indexrxbuf;
            rxbufEndUpDown.Maximum = processing.indexrxbuf;
            rxbufEndUpDown.Value = processing.indexrxbuf;
            updateHistoAndSliders();
            scatterplot.AnScatViewlength = 100000;
            scatterplot.AnScatViewoffset = 0;
            scatterplot.AnScatViewlargeoffset = 0;
            scatterplot.AnScatViewoffsetOld = 0;
            scatterplot.UpdateScatterPlot();
            GC.Collect();
        }

        private void resetoutput()
        {

            int sector, track;
            StringBuilder t = new StringBuilder();

            textBoxReceived.Text = "";

            TrackInfotextBox.Text = "";
            RecoveredSectorsLabel.Text = "";
            RecoveredSectorsWithErrorsLabel.Text = "";

            processing.diskformat = DiskFormat.unknown;
            DiskTypeLabel.Text = "";
            processing.bytespersector = 512;
            processing.sectorspertrack = 0;
            //markerpositionscnt = 0;

            for (track = 0; track < 200; track++)
            {
                for (sector = 0; sector < 18; sector++)
                {
                    processing.sectormap.sectorok[track, sector] = 0;
                }
            }

            Array.Clear(processing.disk, 0, processing.disk.Length);

            processing.sectormap.recoveredsectorcount = 0;
            processing.sectormap.RecoveredSectorWithErrorsCount = 0;
            processing.sectormap.RefreshSectorMap();
        }

        private void ResetInputBtn_Click(object sender, EventArgs e)
        {
            resetinput();
        }

        private void ResetOutputBtn_Click(object sender, EventArgs e)
        {
            resetoutput();
        }

        private void TrackPreset2Button_Click(object sender, EventArgs e)
        {
            StartTrackUpDown.Value = 80;
            EndTracksUpDown.Value = 90;
            TrackDurationUpDown.Value = 540;
        }

        private void updateHistoAndSliders()
        {
            if (!LimitTSCheckBox.Checked)
            {
                //createhistogram();
                updateSliderLabels();
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            TrackDurationUpDown.Value = 5000;
        }

        private void outputfilename_Enter(object sender, EventArgs e)
        {
            disablecatchkey = 1;
        }

        private void outputfilename_Leave(object sender, EventArgs e)
        {
            //tbreceived.Append("Output changed to: "+outputfilename.Text+"\r\n");
            disablecatchkey = 0;
            openFileDialog1.InitialDirectory = subpath + @"\" + outputfilename.Text;
            openFileDialog2.InitialDirectory = subpath + @"\" + outputfilename.Text;
            Properties.Settings.Default["BaseFileName"] = outputfilename.Text;
            Properties.Settings.Default.Save();
        }

        private void FloppyControl_Click(object sender, EventArgs e)
        {
            AddDataButton.Focus();
        }

        private void StopButton_Click(object sender, EventArgs e)
        {
            if (processing.stop == 1)
                processing.stop = 0;
            else
                processing.stop = 1;

            controlfloppy.StopCapture();
            //indexrxbuf = processing.indexrxbuf;
            rxbufStartUpDown.Maximum = processing.indexrxbuf;
            rxbufEndUpDown.Maximum = processing.indexrxbuf;
            rxbufEndUpDown.Value = processing.indexrxbuf;

            tbreceived.Append("Stopping...\r\n");
        }

        private void AboutButton_Click(object sender, EventArgs e)
        {
            MessageBox.Show("This application is created by:\nJosha Beukema.\nCode snippets used from stack overflow and other places.\nAufit DPLL class Copyright (C) 2013-2015 Jean Louis-Guerin. ", "About");
        }

        private void SettingsButton_Click(object sender, EventArgs e)
        {
            SettingsForm SettingsForm1 = new SettingsForm(); // Create instance of settings form
            SettingsForm1.Show();
        }

        private void HDCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (HDCheckBox.Checked)
            {
                ScatterHisto.hd = 1;
                processing.procsettings.hd = 1;
                //hddiv = 2;
            }
            else
            {
                ScatterHisto.hd = 0;
                processing.procsettings.hd = 0;
                //hddiv = 1;
            }
            if (processing.indexrxbuf > 1)
                ScatterHisto.DoHistogram(processing.rxbuf, (int)rxbufStartUpDown.Value, (int)rxbufEndUpDown.Value);
        }

        private void FloppyControl_Resize(object sender, EventArgs e)
        {
            wlabel.Text = this.Width.ToString();
            hlabel.Text = this.Height.ToString();
            /*
            if (this.Width < 1600)
            {
                label6.Hide();
                textBoxReceived.Hide();
            }
            if (this.Width >= 1600)
            {
                label6.Show();
                textBoxReceived.Show();
            }
            */
        }

        private void OffsetScan()
        {
            int i;
            //NormalradioButton.Checked = true;
            ProcessingModeComboBox.SelectedItem = ProcessingType.normal.ToString();
            capturetime = 0;
            processing.processing = 1;
            processing.stop = 0;
            for (i = -15; i < 19; i += 3)
            {
                SettingsLabel.Text = "i = " + i;
                if (processing.stop == 1)
                    break;
                OffsetvScrollBar1.Value = i;
                ScanButton.PerformClick();

                //RefreshSectorMap();
                //this.updateForm();
            }
            processing.stop = 0;
            processing.processing = 0;
        }

        private void OffsetScan2()
        {
            int i;
            ProcessingModeComboBox.SelectedItem = ProcessingType.normal.ToString();
            //NormalradioButton.Checked = true;
            capturetime = 0;
            processing.processing = 1;
            processing.stop = 0;

            for (i = 0; i < 28; i += 1)
            {
                SettingsLabel.Text = "i = " + i;
                if (processing.stop == 1)
                    break;
                OffsetvScrollBar1.Value = ((28 - i) * -1) + 3;
                ScanButton.PerformClick();

                OffsetvScrollBar1.Value = 29 - i;
                ScanButton.PerformClick();

                this.updateForm();
            }

            processing.stop = 0;
            processing.processing = 0;
        }

        

        // Display sector data, only works for PC for now
        private void TrackUpDown2_ValueChanged(object sender, EventArgs e)
        {
            ShowDiskSector();
        }

        private void ShowDiskSector()
        {
            int i, track, sector, offset;
            byte databyte;
            StringBuilder bytesstring = new StringBuilder();
            StringBuilder txtstring = new StringBuilder();

            track = (int)TrackUpDown.Value;
            sector = (int)SectorUpDown.Value;
            offset = (track * 512 * processing.sectorspertrack) + (512 * sector);

            //txtstring.Append();

            for (i = 0; i < 512; i++)
            {
                databyte = (byte)processing.disk[track * 512 * processing.sectorspertrack + (512 * sector) + i];
                bytesstring.Append(databyte.ToString("X2"));
                if (databyte > 32 && databyte < 127)
                    txtstring.Append((char)databyte);
                else txtstring.Append(".");
                if (i % 32 == 31)
                {
                    txtstring.Append("\r\n");
                    bytesstring.Append("\r\n");
                }
            }
            textBoxSector.Text = txtstring.ToString() + "\r\n\r\n";
            textBoxSector.Text += bytesstring.ToString() + "\r\n";
        }

        

        private void SavePrjBtn_Click(object sender, EventArgs e)
        {
            int i, j, sectorcount = 0;
            string extension = ".ADF";
            int ioerror = 0;

            // Write period data to disk in bin format
            extension = ".prj";
            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;

            textBoxReceived.AppendText("Path:" + path + "\r\n");

            bool exists = System.IO.Directory.Exists(path);

            if (processing.diskformat == DiskFormat.amigados)
            {
                sectorcount = 512 * processing.sectorspertrack * 164; // a couple of tracks extra to store possible other data
            }
            else if (processing.diskformat == DiskFormat.diskspare)
            {
                sectorcount = 1024 * 1024; // disk sizes can vary
            }
            else if (processing.diskformat == DiskFormat.pcdd)
            {
                sectorcount = 512 * processing.sectorspertrack * 82 * 2;
            }
            else if (processing.diskformat == DiskFormat.pchd)
            {
                sectorcount = 512 * processing.sectorspertrack * 82 * 2;
            }
            else if (processing.diskformat == DiskFormat.pc2m)
            {
                sectorcount = 2048 * 1024;
            }

            while (File.Exists(path))
            {
                binfilecount++;
                path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;
            }

            //Only save if there's any data to save
            if (sectorcount != 0)
            {
                //if (processing.diskformat == 3 || processing.diskformat == 4) //PC 720 KB dd or 1440KB hd
                //{
                try
                {
                    writer = new BinaryWriter(new FileStream(path, FileMode.Create));
                }
                catch (IOException ex)
                {
                    textBoxReceived.AppendText("IO error: " + ex.ToString());
                    ioerror = 1;
                }
                if (ioerror == 0) // skip writing on io error
                {
                    writer.Write((byte)processing.diskformat);
                    writer.Write((int)sectorcount);
                    //Save sector data
                    for (i = 0; i < sectorcount; i++)
                        writer.Write((byte)processing.disk[i]);

                    //Save sector status
                    for (i = 0; i < 184; i++)
                        for (j = 0; j < 18; j++)
                            writer.Write((byte)processing.sectormap.sectorok[i, j]);

                    if (writer != null)
                    {
                        writer.Flush();
                        writer.Close();
                        writer.Dispose();
                    }
                }
                else ioerror = 0;
                //}
            }
        }

        private void LoadPrjBtn_Click(object sender, EventArgs e)
        {
            openFileDialog2.InitialDirectory = subpath + @"\" + outputfilename.Text;
            openFileDialog2.ShowDialog();
        }

        private void openFileDialog2_FileOk(object sender, CancelEventArgs e)
        {
            int i, j, sectorcount = 0;
            string path1;
            int ioerror = 0;

            textBoxReceived.AppendText("Loading project...\r\n");

            // Write period data to disk in bin format

            path = openFileDialog2.FileName;
            path1 = Path.GetFileName(path);
            outputfilename.Text = path1.Substring(0, path1.IndexOf("_"));
            Properties.Settings.Default["BaseFileName"] = outputfilename.Text;
            Properties.Settings.Default.Save();
            textBoxReceived.AppendText("Path: " + path + "\r\n");

            bool exists = System.IO.Directory.Exists(path);

            /*
            if (processing.diskformat == DiskFormat.amigados) // AmigaDOS
            {
                sectorcount = 512 * processing.sectorspertrack * 160;
            }
            else if (processing.diskformat == DiskFormat.diskspare) // DiskSpare
            {
                sectorcount = 512 * processing.sectorspertrack * 160;
            }
            else if (processing.diskformat == DiskFormat.pcdd) // PC DD
            {
                sectorcount = 512 * processing.sectorspertrack * 80 * 2;
            }
            else if (processing.diskformat == DiskFormat.pchd) // PC HD
            {
                sectorcount = 512 * processing.sectorspertrack * 80 * 2;
            }
            else if (processing.diskformat == DiskFormat.pc2m)
            {
                sectorcount = 2048 * 1024;
            }
            */
            try
            {
                reader = new BinaryReader(new FileStream(path, FileMode.Open));
            }
            catch (IOException ex)
            {
                textBoxReceived.AppendText("IO error: " + ex.ToString() + "\r\n");
                ioerror = 1;
            }
            if (ioerror == 0) // skip writing on io error
            {
                processing.diskformat = (DiskFormat)reader.ReadByte();

                sectorcount = reader.ReadInt32();

                //Load sector data
                for (i = 0; i < sectorcount; i++)
                    processing.disk[i] = reader.ReadByte();

                //Load sector status
                for (i = 0; i < 184; i++)
                    for (j = 0; j < 18; j++)
                        processing.sectormap.sectorok[i, j] = (SectorMapStatus) reader.ReadByte();
                reader.Close();
                reader.Dispose();
            }
            else ioerror = 0;

            textBoxReceived.AppendText("Load complete.\r\n");
            textBoxReceived.AppendText("Sectorcount: " + sectorcount + "\r\n");
            textBoxReceived.AppendText("diskformat:" + processing.diskformat + "\r\n");

            processing.sectormap.RefreshSectorMap();
            //ShowDiskFormat();
        }

        private void ExtremeScan()
        {
            int i, j, k;

            //OffsetvScrollBar1.Value = 0;
            //MinvScrollBar.Value = 0x04;
            //EightvScrollBar.Value = 0xFE;
            int kmax = (int)AddNoiseKnumericUpDown.Value;
            int hd = HDCheckBox.Checked ? 1 : 0;
            int gc_cnt = 0;
            int step = 1 << hd;
            int _4us = FourvScrollBar.Value;
            int _6us = SixvScrollBar.Value;
            processing.stop = 0;
            ProcessingModeComboBox.SelectedItem = ProcessingType.normal.ToString();
            //NormalradioButton.Checked = true;

            uint ESTime;
            ESTime = (uint)(Environment.TickCount + Int32.MaxValue);

            for (i = (int)iESStart.Value; i < (int)iESEnd.Value; i += step)
            {
                //GC.Collect(); // Allow the system to recover some memory
                for (j = (int)jESStart.Value; j < (int)jESEnd.Value; j += step)
                {
                    if (processing.stop == 1)
                        break;

                    FourvScrollBar.Value = _4us + j;
                    SixvScrollBar.Value = _6us + i;

                    if (MinvScrollBar.Value < 4)
                        MinvScrollBar.Value = 4;
                    Application.DoEvents();
                    updateHistoAndSliders();
                    for (k = 0; k < kmax; k++)
                    {
                        SettingsLabel.Text = "i = " + i + " j = " + j + " k = " + k;
                        gc_cnt++;
                        if (gc_cnt % 50 == 0)
                            GC.Collect();
                        if (processing.stop == 1)
                            break;
                        if (processing.diskformat == DiskFormat.amigados || processing.diskformat == DiskFormat.diskspare)
                            processing.StartProcessing(1);
                        else
                            processing.StartProcessing(0);
                        this.updateForm();
                    }
                }
                if (processing.stop == 1)
                    break;
            }
            processing.stop = 0;
            FourvScrollBar.Value = _4us;
            SixvScrollBar.Value = _6us;
            //ESTime = (uint)(Environment.TickCount + Int32.MaxValue);
            tbreceived.Append("Time: " + (uint)(Environment.TickCount + Int32.MaxValue - ESTime) + "ms\r\n");
        }

        private void ECSectorOverlayBtn_Click(object sender, EventArgs e)
        {
            int i, track1, sector1, track2, sector2;
            //int offset = 4;
            //uint databyte;
            StringBuilder bytesstring = new StringBuilder();
            StringBuilder txtstring = new StringBuilder();
            StringBuilder badsecttext = new StringBuilder();
            string key;

            BadSectorListBox.DisplayMember = "name";
            BadSectorListBox.ValueMember = "id";

            track1 = (int)Track1UpDown.Value;
            sector1 = (int)Sector1UpDown.Value;

            track2 = (int)Track2UpDown.Value;
            sector2 = (int)Sector2UpDown.Value;

            BadSectorListBox.Items.Clear();
            JumpTocomboBox.Items.Clear();

            bool goodsectors = GoodSectorsCheckBox.Checked;
            bool badsectors = BadSectorsCheckBox.Checked;

            // First determine if there's bad sectors with the same track and sector
            //int threadid;
            MFMData sectordata;

            for (i = 0; i < processing.sectordata2.Count; i++)
            {
                sectordata = processing.sectordata2[i];
                if (sectordata.track >= track1 && sectordata.track <= track2 &&
                    sectordata.sector >= sector1 && sectordata.sector <= sector2)
                {
                    if ((sectordata.mfmMarkerStatus == SectorMapStatus.HeadOkDataBad) && badsectors)
                    {

                        key = "i: " + i + " B T: " + sectordata.track + " S: " + sectordata.sector;

                        BadSectorListBox.Items.Add(new badsectorkeyval
                        {
                            name = key,
                            id = i,
                            threadid = 0
                        });

                        JumpTocomboBox.Items.Add(new ComboboxItem
                        {
                            Text = key,
                            id = i,
                        });
                    }
                    if ((sectordata.mfmMarkerStatus == SectorMapStatus.CrcOk) && goodsectors)
                    {
                        key = "i: " + i + " G T: " + sectordata.track + " S: " + sectordata.sector;

                        BadSectorListBox.Items.Add(new badsectorkeyval
                        {
                            name = key,
                            id = i,
                            threadid = 0
                        });
                        JumpTocomboBox.Items.Add(new ComboboxItem
                        {
                            Text = key,
                            id = i,
                        });
                    }
                }

                txtstring.Clear();
                bytesstring.Clear();
            }
        }

        private void BadSectorListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            updateECInterface();
        }
        private void updateECInterface()
        {
            ScatterMinUpDown.Value = ScatterMinTrackBar.Value + ScatterOffsetTrackBar.Value;
            ScatterMaxUpDown.Value = ScatterMaxTrackBar.Value + ScatterOffsetTrackBar.Value;
            ScatterOffsetUpDown.Value = ScatterOffsetTrackBar.Value;

            SelectionDifLabel.Text = "Periods:" + (ScatterMaxUpDown.Value - ScatterMinUpDown.Value).ToString();
            BadSectorDraw();
            BadSectorToolTip();
            var currentcontrol = FindFocusedControl(this);
            tabControl1.SelectedTab = ScatterPlottabPage;
            currentcontrol.Focus();
            ShowSectorData();
        }
        private void ShowSectorData()
        {
            int indexS1, threadid;
            int i;
            if (BadSectorListBox.SelectedIndices.Count >= 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;

            }
            else return;

            antbSectorData.Clear();
            antbSectorData.Text = (processing.BytesToHexa(processing.sectordata2[indexS1].sectorbytes, 0, processing.sectordata2[indexS1].sectorbytes.Length));

            int mfmoffset = processing.sectordata2[indexS1].MarkerPositions;
            int length = (processing.sectordata2[indexS1].sectorlength + 1000) * 16;
            //threadid = sectordata[threadid][indexS1].threadid;
            StringBuilder mfmtxt = new StringBuilder();
            for (i = 0; i < length; i++)
            {
                mfmtxt.Append((char)(FDDProcessing.mfms[threadid][i + mfmoffset] + 48));
            }
            ECtbMFM.Text = mfmtxt.ToString();
        }

        private void BadSectorByteDraw()
        {
            int i; //, datapoints, start, end, scrollbarcurrentpos;
            //decimal posx;
            int indexS1 = -1, indexS2 = -1;
            int offset = 4, diskoffset;
            int track, sector;
            byte[] sectors = new byte[1050];
            //int qq;
            int sectorlength = 512;
            int threadid = 0;

            switch ((int)processing.diskformat)
            {
                case 0:
                    return;
                    break;
                case 1:
                    offset = 0;
                    break;
                case 2:
                    offset = 0;
                    break;
                case 3:
                    offset = 4;
                    break;
                case 4:
                    offset = 4;
                    break;
                case 5:
                    offset = 4;
                    break;
            }

            badsectorkeyval badsector1;
            //textBoxReceived.Text += "";
            foreach (int q in BadSectorListBox.SelectedIndices)
            {
                badsector1 = (badsectorkeyval)BadSectorListBox.Items[q];
            }

            if (BadSectorListBox.SelectedIndices.Count == 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                indexS2 = -1;

                if (BSBlueSectormapRadio.Checked)
                {
                    track = processing.sectordata2[indexS1].track;
                    sector = processing.sectordata2[indexS1].sector;

                    diskoffset = track * sectorlength * processing.sectorspertrack + sector * sectorlength;
                    Array.Copy(processing.disk, diskoffset, sectors, 0, sectorlength);
                    offset = 0;
                }
                if (BSBlueFromListRadio.Checked)
                {
                    sectorlength = processing.sectordata2[indexS1].sectorlength;
                    Array.Copy(processing.sectordata2[indexS1].sectorbytes, 0, sectors, 0, sectorlength);

                    //offset = 4;
                }
                if (BlueTempRadio.Checked)
                {
                    Array.Copy(TempSector, 0, sectors, 0, sectorlength + 6);
                    //offset = 4;
                }

                BlueCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS1].crc.ToString("X2");

                if (indexS2 != -1)
                    RedCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS2].crc.ToString("X2");
                else RedCrcCheckLabel.Text = "Crc:";
            }
            else if (BadSectorListBox.SelectedIndices.Count >= 2)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                indexS2 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[1]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                sectorlength = processing.sectordata2[indexS1].sectorlength;

                if (BSBlueFromListRadio.Checked)
                {
                    Array.Copy(processing.sectordata2[indexS1].sectorbytes, 0, sectors, 0, processing.sectordata2[indexS1].sectorbytes.Length);
                    //offset = 4;
                }

                BlueCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS1].crc.ToString("X2");

                if (indexS2 != -1)
                    RedCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS2].crc.ToString("X2");
                else RedCrcCheckLabel.Text = "Crc:";
            }
            else if (BSBlueSectormapRadio.Checked)
            {
                track = (int)Track1UpDown.Value;
                sector = (int)Sector1UpDown.Value;

                diskoffset = track * sectorlength * processing.sectorspertrack + sector * sectorlength;
                Array.Copy(processing.disk, diskoffset, sectors, 0, sectorlength);
                offset = 0;
            }
            else return; // nothing selected, nothing to do

            System.Drawing.Pen BlackPen;
            BlackPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 128, 128, 128));
            System.Drawing.Graphics formGraphics = BadSectorPanel.CreateGraphics();
            System.Drawing.SolidBrush myBrush = new System.Drawing.SolidBrush(Color.White);


            ECHisto.DoHistogram(sectors, offset, sectorlength);
            HistScalingLabel.Text = "Scale: " + ECHisto.getScaling().ToString(); ;

            if (!BSBlueSectormapRadio.Checked) // there's no relevant data when this radio button is checked
            {
                int scatoffset = processing.sectordata2[indexS1].rxbufMarkerPositions + (int)ScatterMinTrackBar.Value + (int)ScatterOffsetTrackBar.Value;
                int scatlength = processing.sectordata2[indexS1].rxbufMarkerPositions + (int)ScatterMaxTrackBar.Value + (int)ScatterOffsetTrackBar.Value - scatoffset;

                scatterplot.AnScatViewlargeoffset = scatoffset;
                scatterplot.AnScatViewoffset = 0;
                scatterplot.AnScatViewlength = scatlength;
                scatterplot.UpdateScatterPlot();
            }
            using (var bmp1 = new System.Drawing.Bitmap(512, 256))
            {
                LockBitmap lockBitmap = new LockBitmap(bmp1);
                lockBitmap.LockBits();

                byte value1 = 0, value2 = 0, colorR = 0, colorB = 0;
                int y = 0;
                int y2, q;

                float f = 512.0f / sectorlength;

                if (f == 0.5f)
                {
                    int qq = 2;
                }

                for (i = 0; i < sectorlength; i++)
                {
                    value1 = sectors[i + offset];
                    if (indexS2 == -1)
                    {
                        colorB = value1;
                        value2 = 0;
                        colorR = 0;
                    }
                    else
                    {
                        value2 = processing.sectordata2[indexS2].sectorbytes[i + offset];
                        if (value1 == value2)
                        {
                            colorR = 0;
                            colorB = value1;
                        }
                        else
                        {
                            colorR = (byte)(128 + (value2 / 2));
                            colorB = value1;
                        }
                    }

                    y2 = 0;
                    for (q = 0; q < 256; q++)
                    {
                        lockBitmap.SetPixel((i % 32) * 16 + (q % 16), (int)(y * 16 * f + (y2 * f)), Color.FromArgb(255, colorR, 0, colorB));
                        if (q % 16 == 15) y2++;
                    }
                    if (i % 32 == 31) y++;
                }

                lockBitmap.UnlockBits();
                formGraphics.DrawImage(bmp1, 0, 0);
            }
            BlackPen.Dispose();
            formGraphics.Dispose();
            myBrush.Dispose();
        }

        private void BadMFMSectorDraw()
        {
            int i; //, datapoints, start, end, scrollbarcurrentpos;
            //decimal posx;
            int indexS1 = -1, indexS2 = -1;
            int offset = 4, diskoffset;
            int track, sector;
            int offsetmfm;
            int offsetmfm2;
            int lengthmfm = 0;
            byte[] sectors = new byte[1050];
            byte[] sectors2 = new byte[1050];
            //int qq;
            int sectorlength = 512;
            int threadid = 0;

            switch (processing.diskformat)
            {
                case DiskFormat.unknown:
                    textBoxReceived.AppendText("\r\nMissing disk format definition, can't draw map. See method BadMFMSectorDraw().\r\n");
                    return;
                    break;
                case DiskFormat.amigados: //AmigaDos
                    offset = 0;
                    lengthmfm = 8704;
                    break;
                case DiskFormat.diskspare://diskspare
                    offset = 0;
                    lengthmfm = 8320;
                    break;
                case DiskFormat.pcdd://pc2m
                case DiskFormat.pc360kb525in:// PC360KB 5.25"
                    offset = -704;
                    lengthmfm = 10464;
                    break;
                case DiskFormat.pchd://pcdd
                    offset = -704;
                    lengthmfm = 10464;
                    break;
                case DiskFormat.pc2m://pchd
                    offset = -704;
                    lengthmfm = 10464;
                    break;
            }

            badsectorkeyval badsector1;
            //textBoxReceived.Text += "";
            foreach (int q in BadSectorListBox.SelectedIndices)
            {
                badsector1 = (badsectorkeyval)BadSectorListBox.Items[q];
            }

            if (BadSectorListBox.SelectedIndices.Count == 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                indexS2 = -1;

                if (BSBlueSectormapRadio.Checked)
                {
                    track = processing.sectordata2[indexS1].track;
                    sector = processing.sectordata2[indexS1].sector;

                    diskoffset = track * sectorlength * processing.sectorspertrack + sector * sectorlength;
                    Array.Copy(processing.disk, diskoffset, sectors, 0, sectorlength);
                    offset = 0;
                }
                if (BSBlueFromListRadio.Checked)
                {
                    threadid = processing.sectordata2[indexS1].threadid;

                    sectorlength = processing.sectordata2[indexS1].sectorlength;
                    //Array.Copy(processing.sectordata2[indexS1].sectorbytes, 0, sectors, 0, sectorlength);
                    offsetmfm = processing.sectordata2[indexS1].MarkerPositions;
                    sectors = processing.MFM2ByteArray(FDDProcessing.mfms[threadid], offsetmfm + offset, lengthmfm);

                    //offset = 4;
                }
                if (BlueTempRadio.Checked)
                {
                    Array.Copy(TempSector, 0, sectors, 0, sectorlength + 6);
                    //offset = 4;
                }

                BlueCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS1].crc.ToString("X2");

                if (indexS2 != -1)
                    RedCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS2].crc.ToString("X2");
                else RedCrcCheckLabel.Text = "Crc:";
            }
            else if (BadSectorListBox.SelectedIndices.Count >= 2)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                indexS2 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[1]]).id;
                //threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;

                threadid = processing.sectordata2[indexS1].threadid;
                sectorlength = processing.sectordata2[indexS1].sectorlength;

                offsetmfm = processing.sectordata2[indexS1].MarkerPositions;
                sectors = processing.MFM2ByteArray(FDDProcessing.mfms[threadid], offsetmfm + offset, lengthmfm);

                threadid = processing.sectordata2[indexS2].threadid;
                offsetmfm2 = processing.sectordata2[indexS2].MarkerPositions;
                sectors2 = processing.MFM2ByteArray(FDDProcessing.mfms[threadid], offsetmfm2 + offset, lengthmfm);

                BlueCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS1].crc.ToString("X2");

                if (indexS2 != -1)
                    RedCrcCheckLabel.Text = "Crc: " + processing.sectordata2[indexS2].crc.ToString("X2");
                else RedCrcCheckLabel.Text = "Crc:";
            }
            else if (BSBlueSectormapRadio.Checked)
            {
                track = (int)Track1UpDown.Value;
                sector = (int)Sector1UpDown.Value;

                diskoffset = track * sectorlength * processing.sectorspertrack + sector * sectorlength;
                Array.Copy(processing.disk, diskoffset, sectors, 0, sectorlength);
                offset = 0;
            }
            else return; // nothing selected, nothing to do

            System.Drawing.Graphics formGraphics = BadSectorPanel.CreateGraphics();

            //ECHisto.DoHistogram(sectors, offset, sectorlength);
            //HistScalingLabel.Text = "Scale: " + ECHisto.getScaling().ToString(); ;

            if (!BSBlueSectormapRadio.Checked) // there's no relevant data when this radio button is checked
            {
                int scatoffset = processing.sectordata2[indexS1].rxbufMarkerPositions + (int)ScatterMinTrackBar.Value + (int)ScatterOffsetTrackBar.Value;
                int scatlength = processing.sectordata2[indexS1].rxbufMarkerPositions + (int)ScatterMaxTrackBar.Value + (int)ScatterOffsetTrackBar.Value - scatoffset;

                scatterplot.AnScatViewlargeoffset = scatoffset;
                scatterplot.AnScatViewoffset = 0;
                scatterplot.AnScatViewlength = scatlength;
                scatterplot.UpdateScatterPlot();
            }
            //StringBuilder mfmbyteEnc = new StringBuilder();

            using (var bmp1 = new System.Drawing.Bitmap(520, 256))
            {
                LockBitmap lockBitmap = new LockBitmap(bmp1);
                lockBitmap.LockBits();

                //lockBitmap.filledsquare(10, 10, 16, 16, Color.FromArgb(255, 0, 0, 255));


                byte value1 = 0, value2 = 0, colorR = 0, colorB = 0;

                int y2, q;
                int width = 40;

                int height = 32; //sectors.Length / width;
                int sectorsindex = 0;
                int w, h, x, y;
                w = 13;
                h = (256 / height);

                float f = 512.0f / sectorlength;

                if (f == 0.5f)
                {
                    int qq = 2;
                }

                if( sectors.Length > 0 )

                for (y = 0; y < 256; y += h)
                {
                    for (x = 0; x < 520; x += w)
                    {
                        //Thread.Sleep(250);
                        //lockBitmap.UnlockBits();
                        //formGraphics.DrawImage(bmp1, 0, 0);
                        //lockBitmap.LockBits();
                        
                        value1 = sectors[sectorsindex];
                        mfmbyteenc[value1]++;
                        //mfmbyteEnc.Append(value1.ToString("X2")+" ");
                        if (indexS2 == -1)
                        {
                            colorB = value1;
                            value2 = 0;
                            colorR = 0;
                        }
                        else
                        {
                            value2 = sectors2[sectorsindex];
                            if (value1 == value2)
                            {
                                colorR = 0;
                                colorB = value1;
                            }
                            else
                            {
                                colorR = (byte)(128 + (value2 / 2));
                                colorB = value1;
                            }
                        }

                        sectorsindex++;
                        if (sectorsindex >= sectors.Length)
                            break;

                        lockBitmap.filledsquare(x, y, w, h, Color.FromArgb(255, colorR, 0, colorB));

                    }
                    if (sectorsindex >= sectors.Length)
                        break;
                }

                lockBitmap.UnlockBits();
                formGraphics.DrawImage(bmp1, 0, 0);
            }
            formGraphics.Dispose();
            //tbSectorMap.AppendText(mfmbyteEnc.ToString());
        }


        private void BadSectorPanel_MouseDown(object sender, MouseEventArgs e)
        {
            int indexS1;//, indexS2;
            int offset = 4;
            int diskoffset;
            int x, y;
            int bsbyte;
            int track, sectornr;
            int datacrc;

            int i;
            int threadid;

            BadSectorTooltipPos = BadSectorPanel.PointToClient(Cursor.Position);

            if (BadSectorListBox.SelectedIndices.Count >= 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;

                int sectorlength = processing.sectordata2[indexS1].sectorlength;
                if (sectorlength < 512)
                {
                    tbreceived.Append("sector length is less than 512 bytes!!");
                    return;
                }

                if (ECMFMcheckBox.Checked)
                {
                    int w = 13;
                    int h = 8;
                    int lengthmfm;
                    int mfmoffset2 = 0;

                    switch ((int)processing.diskformat)
                    {
                        case 0:
                            return;
                            break;
                        case 1: //AmigaDos
                            offset = -48;
                            lengthmfm = 8704;
                            break;
                        case 2://diskspare
                            offset = -16;
                            lengthmfm = 8320;
                            break;
                        case 3://pcdd
                            offset = -712;
                            lengthmfm = 10464;
                            mfmoffset2 = -712;
                            break;
                        case 4://pchd
                            offset = -712;
                            lengthmfm = 10464;
                            mfmoffset2 = -712;
                            break;
                        case 5://pc2m
                            offset = -712;
                            lengthmfm = 10464;
                            mfmoffset2 = -712;
                            break;
                    }
                    //if (f == 0.0f) f = 1;
                    x = ((BadSectorTooltipPos.X) / w);
                    y = (int)(BadSectorTooltipPos.Y / h);
                    //int offset;


                    int mfmoffset = processing.sectordata2[indexS1].MarkerPositions;
                    bsbyte = ((y * 40 + x) * 8) + offset;
                    MFMByteStartUpDown.Value = ((y * 40 + x) * 8) + mfmoffset2;
                    int indexcnt = 0;
                    if (bsbyte > 0)
                    {
                        for (i = 0; i < bsbyte; i++)
                        {
                            if (FDDProcessing.mfms[processing.sectordata2[indexS1].threadid][i + mfmoffset] == 1)
                            {
                                indexcnt++;
                            }
                        }
                    }
                    else
                    {
                        for (i = bsbyte; i < 0; i++)
                        {
                            if (FDDProcessing.mfms[processing.sectordata2[indexS1].threadid][i + mfmoffset] == 1)
                            {
                                indexcnt--;
                            }
                        }
                    }
                    tbreceived.Append("index:" + indexcnt + "\r\n");
                    ScatterMinTrackBar.Value = indexcnt;
                    ScatterMaxTrackBar.Value = indexcnt + 14;
                    updateECInterface();
                }
                else
                {
                    int f = sectorlength / 512;

                    x = ((BadSectorTooltipPos.X) / 16);
                    y = (int)((BadSectorTooltipPos.Y) / (16 / f));

                    bsbyte = y * 32 + x;
                    // Temporary decouple event handler
                    byteinsector = bsbyte;
                    BSEditByteLabel.Text = "Byte: " + bsbyte;

                    // Zoom in scatterplot
                    int indexcnt = 0;
                    int mfmoffset = processing.sectordata2[indexS1].MarkerPositions;
                    // First find the period index
                    for (i = 0; i < (bsbyte + 4) * 16; i++)
                    {
                        if (FDDProcessing.mfms[processing.sectordata2[indexS1].threadid][i + mfmoffset] == 1)
                        {
                            indexcnt++;
                        }
                    }
                    tbreceived.Append("index:" + indexcnt + "\r\n");
                    ScatterMinTrackBar.Value = indexcnt;
                    ScatterMaxTrackBar.Value = indexcnt + 14;
                    updateECInterface();
                    if ((int)BluetoRedByteCopyToolBtn.Tag == 1)
                    {
                        // Copy single byte from BadSectors to disk array
                        if (BSBlueSectormapRadio.Checked)
                        {
                            textBoxReceived.AppendText("Copy byte to disk array.");
                            track = processing.sectordata2[indexS1].track;
                            sectornr = processing.sectordata2[indexS1].sector;
                            datacrc = processing.sectordata2[indexS1].crc;

                            processing.sectorspertrack = 9;

                            //(tracknr * processing.sectorspertrack * 512 * 2) + (headnr * processing.sectorspertrack * 512) + (sectornr * 512);
                            diskoffset = track * processing.sectorspertrack * 512 + sectornr * 512;
                            processing.disk[diskoffset] = processing.sectordata2[indexS1].sectorbytes[bsbyte + offset];
                        }

                        //Copy byte from BadSectors to TempSector
                        if (BlueTempRadio.Checked)
                        {
                            textBoxReceived.AppendText("Copy byte to Temp.");
                            track = processing.sectordata2[indexS1].track;
                            sectornr = processing.sectordata2[indexS1].sector;
                            datacrc = processing.sectordata2[indexS1].crc;

                            processing.sectorspertrack = 9;

                            //(tracknr * processing.sectorspertrack * 512 * 2) + (headnr * processing.sectorspertrack * 512) + (sectornr * 512);
                            diskoffset = track * processing.sectorspertrack * 512 + sectornr * 512;
                            TempSector[bsbyte + offset] = processing.sectordata2[indexS1].sectorbytes[bsbyte + offset];
                        }

                        //Check crc
                        ushort datacrcchk;
                        Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero1);
                        datacrcchk = crc.ComputeChecksum(TempSector);
                        BlueCrcCheckLabel.Text = "Crc: " + datacrcchk.ToString("X2");

                        processing.sectordata2[indexS1].crc = datacrcchk;
                    }
                }
            }
        }

        private void CopySectorToBlueBtn_Click(object sender, EventArgs e)
        {
            int i;
            int indexS1; //indexS2;
            int offset = 4;
            int diskoffset;
            //int x, y;
            //int bsbyte;
            int track, sectornr;
            int datacrc;
            //int processing.sectorspertrack;
            int threadid;

            switch ((int)processing.diskformat)
            {
                case 0:
                    return;
                    break;
                case 1:
                    offset = 0;
                    break;
                case 2:
                    offset = 0;
                    break;
                case 3:
                    offset = 4;
                    break;
                case 4:
                    offset = 4;
                    break;
                case 5:
                    offset = 4;
                    break;
            }

            if (BadSectorListBox.SelectedIndices.Count == 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;

                // Copy sector from BadSectors to disk array
                if (BSBlueSectormapRadio.Checked && BSRedFromlistRadio.Checked)
                {
                    textBoxReceived.AppendText("Copy single sector to disk array.");
                    track = processing.sectordata2[indexS1].track;
                    sectornr = processing.sectordata2[indexS1].sector;
                    datacrc = processing.sectordata2[indexS1].crc;
                    //processing.sectorspertrack = 9;

                    //(tracknr * processing.sectorspertrack * 512 * 2) + (headnr * processing.sectorspertrack * 512) + (sectornr * 512);
                    diskoffset = track * processing.sectorspertrack * 512 + sectornr * 512;

                    for (i = 0; i < 512; i++)
                    {
                        processing.disk[diskoffset + i] = processing.sectordata2[indexS1].sectorbytes[i + offset];
                    }
                }

                // Copy sector from BadSectors to temporary sector buffer
                if (BlueTempRadio.Checked && BSRedFromlistRadio.Checked)
                {
                    textBoxReceived.AppendText("Copy full sector to Temp.");
                    track = processing.sectordata2[indexS1].track;
                    sectornr = processing.sectordata2[indexS1].sector;
                    datacrc = processing.sectordata2[indexS1].crc;

                    //processing.sectorspertrack = 9;

                    // I combined tracks and head to simplify stuff
                    // My track = tracks * 2 + headnr
                    // track 10 head 1 is 21
                    diskoffset = track * processing.sectorspertrack * 512 + sectornr * 512;

                    for (i = 0; i < 518; i++)
                    {
                        TempSector[i] = processing.sectordata2[indexS1].sectorbytes[i];
                    }

                    //Check crc
                    ushort datacrcchk;
                    Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero1);
                    datacrcchk = crc.ComputeChecksum(TempSector);
                    BlueCrcCheckLabel.Text = "Crc: " + datacrcchk.ToString("X2");

                    processing.sectordata2[indexS1].crc = datacrcchk;
                }

                // Copy sector from temporary sector buffer to disk array
                if (BlueTempRadio.Checked && BSRedTempRadio.Checked)
                {
                    textBoxReceived.AppendText("Copy full sector to Temp.");
                    track = processing.sectordata2[indexS1].track;
                    sectornr = processing.sectordata2[indexS1].sector;
                    datacrc = processing.sectordata2[indexS1].crc;

                    //processing.sectorspertrack = 9;

                    //(tracknr * processing.sectorspertrack * 512 * 2) + (headnr * processing.sectorspertrack * 512) + (sectornr * 512);
                    diskoffset = track * processing.sectorspertrack * 512 + sectornr * 512;

                    for (i = 0; i < 512; i++)
                    {
                        processing.disk[diskoffset + i] = TempSector[i + offset];
                    }

                    //Do crc check
                    ushort datacrcchk;
                    Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero1);
                    datacrcchk = crc.ComputeChecksum(TempSector);
                    BlueCrcCheckLabel.Text = "Crc: " + datacrcchk.ToString("X2");

                    processing.sectordata2[indexS1].crc = datacrcchk;
                }
            }
        }
      
        private void BadSectorPanel_MouseHover(object sender, EventArgs e)
        {
            BadSectorToolTip();
        }

        private void BadSectorPanel_MouseMove(object sender, MouseEventArgs e)
        {
            BadSectorToolTip();
        }
        private void BadSectorToolTip()
        {
            int x, y, bsbyte, indexS1 = 0, indexS2;
            int offset = 4;
            int sectorlength;
            int threadid = 0;

            switch ((int)processing.diskformat)
            {
                case 0:
                    return;
                    break;
                case 1:
                    offset = 0;
                    break;
                case 2:
                    offset = 0;
                    break;
                case 3:
                    offset = 4;
                    break;
                case 4:
                    offset = 4;
                    break;
                case 5:
                    offset = 4;
                    break;
            }

            if (ECMFMcheckBox.Checked)
            {
                //if( processing.diskformat == DiskFormat.amigados || processing.diskformat == DiskFormat.diskspare || processing.diskformat == )
                if (BadSectorListBox.SelectedIndices.Count == 1)
                {
                    indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                    indexS2 = -1;
                    threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                }
                else if (BadSectorListBox.SelectedIndices.Count >= 2)
                {
                    indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                    indexS2 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[1]]).id;
                    threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                }
                else return;
                if (processing.sectordata2 == null) return;
                if (processing.sectordata2.Count == 0) return;
                sectorlength = processing.sectordata2[indexS1].sectorlength;
                BadSectorTooltipPos = BadSectorPanel.PointToClient(Cursor.Position);
                //int f = sectorlength / 512;
                int w = 13;
                int h = 8;
                int lengthmfm;
                switch ((int)processing.diskformat)
                {
                    case 0:
                        return;
                        break;
                    case 1: //AmigaDos
                        offset = 0;
                        lengthmfm = 8704;
                        break;
                    case 2://diskspare
                        offset = 0;
                        lengthmfm = 8320;
                        break;
                    case 3://pc2m
                        offset = -704;
                        lengthmfm = 10464;
                        break;
                    case 4://pcdd
                        offset = -704;
                        lengthmfm = 10464;
                        break;
                    case 5://pchd
                        offset = -704;
                        lengthmfm = 10464;
                        break;
                }
                //if (f == 0.0f) f = 1;
                x = ((BadSectorTooltipPos.X) / w);
                y = (int)(BadSectorTooltipPos.Y / h);
                bsbyte = (y * 40 + x);

                //if (bsbyte > sectorlength - 1) return;

                if (BadSectorTooltipPos.X < 350)
                    BadSectorTooltipPos.X += 30;
                else BadSectorTooltipPos.X -= 150;
                int mfmoffset = bsbyte * 8 + offset;
                if (mfmoffset < offset) return;
                int mfmmarkerposition = processing.sectordata2[indexS1].MarkerPositions;
                threadid = processing.sectordata2[indexS1].threadid;
                byte[] mfm = processing.MFM2ByteArray(FDDProcessing.mfms[threadid], mfmmarkerposition + mfmoffset, 256);
                BadSectorTooltip.Text = " Offset: " + (mfmoffset) + " = " + mfm[0].ToString("X2"); ;
                BadSectorTooltip.Show();

            }
            else
            {

                //if( processing.diskformat == DiskFormat.amigados || processing.diskformat == DiskFormat.diskspare || processing.diskformat == )
                if (BadSectorListBox.SelectedIndices.Count == 1)
                {
                    indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                    indexS2 = -1;
                    threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                }
                else if (BadSectorListBox.SelectedIndices.Count >= 2)
                {
                    indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                    indexS2 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[1]]).id;
                    threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                }
                else return;
                if (processing.sectordata2 == null) return;
                if (processing.sectordata2.Count == 0) return;
                sectorlength = processing.sectordata2[indexS1].sectorlength;
                BadSectorTooltipPos = BadSectorPanel.PointToClient(Cursor.Position);
                int f = sectorlength / 512;
                if (f == 0.0f) f = 1;
                x = ((BadSectorTooltipPos.X) / 16);
                y = (int)((BadSectorTooltipPos.Y) / (16 / f));
                bsbyte = y * 32 + x;

                if (bsbyte > sectorlength - 1) return;

                if (BadSectorTooltipPos.X < 350)
                    BadSectorTooltipPos.X += 30;
                else BadSectorTooltipPos.X -= 150;

                //BadSectors[indexS1][i + offset];
                //BadSectorTooltip.Text = "X: " + x + " Y:" + y + " byte: " + bsbyte;
                if (bsbyte >= 0 && bsbyte <= (sectorlength + 6) - 4)
                {
                    BadSectorTooltip.Text = " byte: " + bsbyte + " = " + processing.sectordata2[indexS1].sectorbytes[bsbyte + offset].ToString("X2");
                    BadSectorTooltip.Show();
                }
            }
        }
        private void BadSectorPictureBox_Paint(object sender, PaintEventArgs e)
        {
            BadSectorDraw();
        }

        private void BadSectorDraw()
        {
            if (ECMFMcheckBox.Checked)
                BadMFMSectorDraw();
            else
                BadSectorByteDraw();
        }

        private void BadSectorPanel_MouseLeave(object sender, EventArgs e)
        {
            BadSectorTooltip.Hide();
        }

        private void timer5_Tick(object sender, EventArgs e)
        {
            BadSectorTooltip.Location = BadSectorTooltipPos;
        }

        private void BluetoRedByteCopyToolBtn_Click(object sender, EventArgs e)
        {
            if ((int)(BluetoRedByteCopyToolBtn.Tag) == 0)
            {
                BluetoRedByteCopyToolBtn.Tag = 1; // Button is active
                BluetoRedByteCopyToolBtn.BackColor = Color.FromArgb(255, 255, 208, 192);
            }
            else
            {
                BluetoRedByteCopyToolBtn.Tag = 0; // Button is active
                BluetoRedByteCopyToolBtn.BackColor = SystemColors.Control;
            }
        }

        private void BSBlueFromListRadio_CheckedChanged(object sender, EventArgs e)
        {
            BadSectorDraw();
        }

        private void BSBlueSectormapRadio_CheckedChanged(object sender, EventArgs e)
        {
            BadSectorDraw();
        }

        private void BlueTempRadio_CheckedChanged(object sender, EventArgs e)
        {
            BadSectorDraw();
        }

        private void Track1UpDown_ValueChanged(object sender, EventArgs e)
        {
            BadSectorDraw();
        }

        private void Sector1UpDown_ValueChanged(object sender, EventArgs e)
        {
            BadSectorDraw();
        }

        // Convert MFM in text to MFM in bytes to decoded bytes to hex
        private void button2_Click(object sender, EventArgs e)
        {
            int i;

            //byte[] bytes = new byte[] { 65, 66, 67, 68 };//Encoding.ASCII.GetBytes(tbBIN.Text);

            //byte[] bytes = HexToBytes(tbBIN.Text);
            //byte[] mfmbytes = BIN2MFMbits(ref bytes, bytes.Count(), 0, false);
            byte[] bytebuf = new byte[tbMFM.Text.Length / 8];

            byte[] mfmbytes = new byte[tbMFM.Text.Length];
            byte[] mfmbytes2 = new byte[tbMFM.Text.Length];

            //tbMFM.Text = Encoding.ASCII.GetString(BIN2MFMbits(ref bytes, bytes.Count(), 0, true));

            mfmbytes = Encoding.ASCII.GetBytes(tbMFM.Text);

            int cnt = 0;

            for (i = 0; i < mfmbytes.Length; i++)
            {
                if (mfmbytes[i] == 48 || mfmbytes[i] == 49)
                    mfmbytes2[cnt++] = (byte)(mfmbytes[i] - 48); // from ascii to byte
            }

            StringBuilder tbt = new StringBuilder();
            StringBuilder txt = new StringBuilder();
            for (i = 0; i < mfmbytes2.Length / 16; i++)
            {
                bytebuf[i] = processing.MFMBits2BINbyte(ref mfmbytes2, (i * 16));
                tbt.Append(bytebuf[i].ToString("X2"));
                if (bytebuf[i] > ' ' && bytebuf[i] < 127) txt.Append((char)bytebuf[i]);
                else txt.Append(".");

            }
            tbTest.Clear();
            AntxtBox.Clear();
            tbTest.AppendText(tbt.ToString());
            AntxtBox.AppendText(txt.ToString());
        }

        private void panel2_Paint(object sender, PaintEventArgs e)
        {
        }

        private void GUITimer_Tick(object sender, EventArgs e)
        {
            ProcessStatusLabel.Text = FDDProcessing.ProcessStatus[FDDProcessing.mfmsindex];
            progressBar1.Minimum = FDDProcessing.progressesstart[FDDProcessing.mfmsindex];
            progressBar1.Maximum = FDDProcessing.progressesend[FDDProcessing.mfmsindex];

            if (FDDProcessing.progresses[FDDProcessing.mfmsindex] >= FDDProcessing.progressesstart[FDDProcessing.mfmsindex] && 
                FDDProcessing.progresses[FDDProcessing.mfmsindex] <= FDDProcessing.progressesend[FDDProcessing.mfmsindex])
                if (FDDProcessing.progresses[FDDProcessing.mfmsindex] <= progressBar1.Maximum && 
                    FDDProcessing.progresses[FDDProcessing.mfmsindex] >= progressBar1.Minimum)
                    progressBar1.Value = FDDProcessing.progresses[FDDProcessing.mfmsindex];

            textBoxReceived.AppendText(tbreceived.ToString());
            tbreceived.Clear();
            this.updateForm();
        }

        private void MainTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            //tbreceived.Append("Tab"+MainTabControl.SelectedTab.Name+" Index: "+MainTabControl.SelectedIndex+"\r\n");
            if (MainTabControl.SelectedIndex == 2)
            {
                //MainTabControl.TabPages[1].Controls.Remove(ThresholdsGroupBox);
                MainTabControl.TabPages[2].Controls.Add(ThresholdsGroupBox);
                ThresholdsGroupBox.Location = new Point(459, 290);


            }
            else
            {
                groupBox6.Controls.Add(ThresholdsGroupBox);
                ThresholdsGroupBox.Location = new Point(600, 16);
            }
        }

        private void button21_Click(object sender, EventArgs e)
        {
            byte[] bytebuf = Encoding.ASCII.GetBytes(tbBIN.Text);

            ushort datacrcchk;
            Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero1);
            datacrcchk = crc.ComputeChecksum(bytebuf);
            tbTest.AppendText("CRC: " + datacrcchk.ToString("X4") + "\r\n");
        }

        private void button23_Click(object sender, EventArgs e)
        {
            byte[] bytes = FDDProcessing.HexToBytes(tbBIN.Text);
            ushort datacrcchk;
            Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero1);
            datacrcchk = crc.ComputeChecksum(bytes);
            tbTest.AppendText("CRC: " + datacrcchk.ToString("X4") + "\r\n");
        }

        private void button26_Click(object sender, EventArgs e)
        {
            byte[] bytebuf = Encoding.ASCII.GetBytes(tbBIN.Text);

            ushort datacrcchk;
            Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero3);
            datacrcchk = crc.ComputeGoodChecksum(bytebuf);
            tbTest.AppendText("CRC: " + datacrcchk.ToString("X4") + "\r\n");
        }

        private void button25_Click(object sender, EventArgs e)
        {
            byte[] bytes = FDDProcessing.HexToBytes(tbBIN.Text);
            ushort datacrcchk;
            Crc16Ccitt crc = new Crc16Ccitt(InitialCrcValue.NonZero3);
            datacrcchk = crc.ComputeGoodChecksum(bytes);
            tbTest.AppendText("CRC: " + datacrcchk.ToString("X4") + "\r\n");
        }

        private void ScatterMinUpDown_ValueChanged(object sender, EventArgs e)
        {
            //ScatterMinTrackBar.Value = (int)ScatterMinUpDown.Value;
            //ScatterMaxTrackBar.Value = (int)ScatterMaxUpDown.Value;
        }

        private void ThreadsUpDown_ValueChanged(object sender, EventArgs e)
        {
            processing.NumberOfThreads = (int)ThreadsUpDown.Value;
        }

        private void BadSectorListBox_KeyDown(object sender, KeyEventArgs e)
        {
            //tbreceived.Append("KeyCode: "+(int)e.KeyCode+"\r\n");
            if (e.KeyCode == Keys.Delete) //Delete key
            {
                var selectedItems = BadSectorListBox.SelectedItems;
                var qq = selectedItems[0];
                if (BadSectorListBox.SelectedIndex != -1)
                {
                    for (int i = selectedItems.Count - 1; i >= 0; i--)
                    {
                        var badsectoritem = (badsectorkeyval)selectedItems[i];

                        for (int j = 0; j < JumpTocomboBox.Items.Count; j++)
                        {
                            var jumpboxitem = (ComboboxItem)JumpTocomboBox.Items[j];

                            if (jumpboxitem.id == badsectoritem.id)
                                JumpTocomboBox.Items.RemoveAt(j);
                        }

                        BadSectorListBox.Items.Remove(selectedItems[i]);
                    }
                }
            }
        }

        private void ECZoomOutBtn_Click(object sender, EventArgs e)
        {
            int indexS1, threadid;

            if (BadSectorListBox.SelectedIndices.Count >= 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;

                int sectorlength = processing.sectordata2[indexS1].sectorlength;

                int factor = sectorlength / 512;

                ScatterMinTrackBar.Value = 0;
                ScatterMaxTrackBar.Value = 4500 * factor;
                updateECInterface();
            }
        }

        private void ECRealign4E_Click(object sender, EventArgs e)
        {
            int indexS1, listlength = 0, i, threadid;
            var selected = BadSectorListBox.SelectedIndices;
            listlength = selected.Count;

            ECSettings ecSettings = new ECSettings();
            ECResult sectorresult;
            ecSettings.sectortextbox = textBoxSector;

            if (ScatterMaxTrackBar.Value - ScatterMinTrackBar.Value > 50)
            {
                tbreceived.Append("Error: selection can't be larger than 50!\r\n");
                return;
            }
            
            if (listlength >= 1)
            {
                for (i = 0; i < listlength; i++)
                {
                    if (processing.stop == 1)
                        break;
                    indexS1 = ((badsectorkeyval)BadSectorListBox.Items[selected[i]]).id;
                    threadid = ((badsectorkeyval)BadSectorListBox.Items[selected[i]]).threadid;
                    ecSettings.indexS1 = indexS1;
                    ecSettings.periodSelectionStart = (int)ScatterMinUpDown.Value;
                    ecSettings.periodSelectionEnd = (int)ScatterMaxUpDown.Value;
                    ecSettings.threadid = threadid;
                    if ((int)processing.diskformat > 2)
                    {
                        sectorresult = processing.ProcessRealign4E(ecSettings);
                        if (sectorresult != null)
                        {
                            AddRealignedToLists(sectorresult);
                        }
                    }
                    else
                    {
                        sectorresult = processing.ProcessRealignAmiga(ecSettings);
                        if (sectorresult != null)
                        {
                            AddRealignedToLists(sectorresult);
                        }
                    }
                }
            }
            else
            {
                textBoxReceived.AppendText("Error, no data selected.");
                return;
            }
        }

        private void AddRealignedToLists(ECResult sectorresult)
        {
            MFMData sectordata = sectorresult.sectordata;
            int badsectorcnt2 = sectorresult.index;
            int track = sectordata.track;
            int sector = sectordata.sector;

            var currentcontrol = FindFocusedControl(this);
            tabControl1.SelectedTab = ShowSectorTab;
            currentcontrol.Focus();

            string key = "Aligned: T" + track + " s" + sector;
            int index = BadSectorListBox.Items.Add(new badsectorkeyval
            {
                name = "i: " + badsectorcnt2 + " " + key,
                id = badsectorcnt2,
                threadid = sectordata.threadid
            });
            //JumpTocomboBox.Items.Add()
            int index2 = JumpTocomboBox.Items.Add(new ComboboxItem
            {
                Text = "i: " + badsectorcnt2 + " " + key,
                id = badsectorcnt2,
            });
        }

        public static Control FindFocusedControl(Control control)
        {
            var container = control as IContainerControl;
            while (container != null)
            {
                control = container.ActiveControl;
                container = control as IContainerControl;
            }
            return control;
        }

        private void button20_Click_1(object sender, EventArgs e)
        {
            //int i;
            StringBuilder tbt = new StringBuilder();
            StringBuilder txt = new StringBuilder();

            // Convert string of hex encoded ascii to byte array
            byte[] bytes = FDDProcessing.HexToBytes(tbBIN.Text);

            byte[] mfmbytes;

            // Convert bytes to Amiga mfm
            mfmbytes = processing.amigamfmencodebytes(bytes, 0, bytes.Length);

            byte[] checksum;

            checksum = processing.amigachecksum(mfmbytes, 0, mfmbytes.Length);

            tbt.Append("Checksum:" + checksum[0].ToString("X2") + checksum[1].ToString("X2") + checksum[2].ToString("X2") + checksum[3].ToString("X2"));

            tbTest.Clear();
            AntxtBox.Clear();
            tbTest.AppendText(tbt.ToString());
            AntxtBox.AppendText(txt.ToString());
        }

        private void AuScan()
        {
            int i;
            processing.stop = 0;
            //AufitRadioButton.Checked = true;
            ProcessingModeComboBox.SelectedItem = ProcessingType.aufit.ToString();
            scanactive = true;
            for (i = 0x2E; i < 0x3A; i += 2)
            {
                SettingsLabel.Text = "1. i = " + i;
                if (processing.stop == 1)
                    break;
                MinvScrollBar.Value = i;
                if ((int)processing.diskformat <= 2)
                    processing.StartProcessing(1);
                else
                    processing.StartProcessing(0);
                processing.sectormap.RefreshSectorMap();
                this.updateForm();
            }
            MinvScrollBar.Value = 0x32;
            for (i = 0x07; i < 0x18; i += 2)
            {
                SettingsLabel.Text = "2. i = " + i;
                if (processing.stop == 1)
                    break;
                FourvScrollBar.Value = i;
                if ((int)processing.diskformat <= 2)
                    processing.StartProcessing(1);
                else
                    processing.StartProcessing(0);
                processing.sectormap.RefreshSectorMap();
                this.updateForm();
            }

            scanactive = false;
            processing.stop = 0;
        }

        private void outputfilename_TextChanged(object sender, EventArgs e)
        {
            tbreceived.Append("Output changed to: " + outputfilename.Text + "\r\n");
            openFileDialog1.InitialDirectory = subpath + @"\" + outputfilename.Text;
            openFileDialog2.InitialDirectory = subpath + @"\" + outputfilename.Text;
            
            Properties.Settings.Default["BaseFileName"] = outputfilename.Text;
            Properties.Settings.Default.Save();
        }

        private void CreateGraphs()
        {
            graphset.SetAllChanged();
            var gr = graphset.Graphs;
            if (graphset.Graphs.Count < 4)
            {
                int cnt = graphset.Graphs.Count;
                int channels = 4;
                int i;

                if (cnt < channels)
                {
                    for (i = 0; i < channels - cnt; i++)
                    {
                        graphset.AddGraph(new byte[gr[0].data.Length]);
                        graphset.Graphs[graphset.Graphs.Count - 1].datalength = graphset.Graphs[0].datalength;
                        graphset.Graphs[graphset.Graphs.Count - 1].density = graphset.Graphs[0].density;
                        graphset.Graphs[graphset.Graphs.Count - 1].dataoffset = graphset.Graphs[0].dataoffset;
                    }
                }
            }
            if (graphset.Graphs.Count >= 4)
            {
                graphset.Graphs[0].yoffset = -200;
                //graphset.Graphs[0].yscale = 2.86f;

                graphset.Graphs[1].yoffset = 0;
                graphset.Graphs[1].yscale = 0.36f;

                graphset.Graphs[2].yoffset = 0;
                graphset.Graphs[2].yscale = 5;

                graphset.Graphs[3].yoffset = 175;
                graphset.Graphs[3].yscale = 1;
            }
            if (graphset.Graphs.Count >= 5)
            {
                gr[0].zorder = 10;
                gr[4].zorder = 9;
                var src = graphset.Graphs[0];
                var dst = graphset.Graphs[4];
                dst.datalength = src.datalength;
                dst.dataoffset = src.dataoffset;
                dst.density = src.density;
                //graphset.Graphs[0].yoffset = -200;
                //graphset.Graphs[0].yscale = 2.86f;
                dst.yscale = src.yscale;
                dst.yoffset = src.yoffset;
                src.zorder = 10;
                dst.zorder = 9;
            }

            if (graphset.Graphs[0].datalength == 0)
                graphset.Graphs[0].datalength = graphset.Graphs[0].data.Length;

            if (!(graphset.Graphs[0].dataoffset < graphset.Graphs[0].data.Length - graphset.Graphs[0].datalength))
            {
                int datalength = gr[0].data.Length - 1;
                int density = (int)Math.Log(datalength / 512.0, 1.4f);//datalength/graph[0].width;
                if (density <= 0) density = 1;
                if (datalength < 1000) density = 1;
                AnDensityUpDown.Value = density;
                GraphLengthLabel.Text = gr[0].data.Length.ToString();//DataLengthTrackBar.Value.ToString();

                for (int i = 0; i < gr.Count; i++)
                {
                    graphset.Graphs[i].dataoffset = 0;
                    graphset.Graphs[i].datalength = datalength;
                    graphset.Graphs[i].density = density;
                }
            }

            GraphLengthLabel.Text = (gr[0].data.Length - 1000).ToString();
            Graph1SelRadioButton.Checked = true;
            graphset.UpdateGraphs();

        }
        private void GraphOffsetTrackBar_Scroll(object sender, EventArgs e)
        {
            int index = 0;
            if (Graph1SelRadioButton.Checked) index = 0;
            else
            if (Graph2SelRadioButton.Checked) index = 1;
            else
            if (Graph3SelRadioButton.Checked) index = 2;
            else
            if (Graph4SelRadioButton.Checked) index = 3;
            else
            if (Graph5SelRadioButton.Checked) index = 4;
            graphset.Graphs[index].changed = true;

            graphset.Graphs[graphselect].yoffset = GraphOffsetTrackBar.Value;
            graphset.UpdateGraphs();
            GraphYOffsetlabel.Text = GraphOffsetTrackBar.Value.ToString();
        }

        private void trackBar3_Scroll(object sender, EventArgs e)
        {
            int index = 0;
            if (Graph1SelRadioButton.Checked) index = 0;
            else
            if (Graph2SelRadioButton.Checked) index = 1;
            else
            if (Graph3SelRadioButton.Checked) index = 2;
            else
            if (Graph4SelRadioButton.Checked) index = 3;
            else
            if (Graph5SelRadioButton.Checked) index = 4;

            graphset.Graphs[index].changed = true;

            graphset.Graphs[graphselect].yscale = (GraphYScaleTrackBar.Value / 100.0f);
            graphset.UpdateGraphs();
            GraphScaleYLabel.Text = (GraphYScaleTrackBar.Value / 100.0f).ToString();
        }

        private void Graph1SelRadioButton_CheckedChanged(object sender, EventArgs e)
        {

            if (graphset.Graphs.Count >= 1)
            {
                graphselect = 0;
                GraphOffsetTrackBar.Value = graphset.Graphs[graphselect].yoffset;
                GraphYScaleTrackBar.Value = (int)(graphset.Graphs[graphselect].yscale * 100);
                graphset.UpdateGraphs();
            }
        }

        private void Graph2SelRadioButton_CheckedChanged(object sender, EventArgs e)
        {

            if (graphset.Graphs.Count >= 2)
            {

                graphselect = 1;
                GraphOffsetTrackBar.Value = graphset.Graphs[graphselect].yoffset;
                GraphYScaleTrackBar.Value = (int)(graphset.Graphs[graphselect].yscale * 100);
                graphset.UpdateGraphs();
            }
        }

        private void Graph3SelRadioButton_CheckedChanged(object sender, EventArgs e)
        {


            if (graphset.Graphs.Count >= 3)
            {
                graphselect = 2;
                GraphOffsetTrackBar.Value = graphset.Graphs[graphselect].yoffset;
                GraphYScaleTrackBar.Value = (int)(graphset.Graphs[graphselect].yscale * 100);
                graphset.UpdateGraphs();
            }
        }

        private void Graph4SelRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (graphset.Graphs.Count >= 4)
            {
                graphselect = 3;
                GraphOffsetTrackBar.Value = graphset.Graphs[graphselect].yoffset;
                GraphYScaleTrackBar.Value = (int)(graphset.Graphs[graphselect].yscale * 100);
                graphset.UpdateGraphs();
            }
        }

        private void Graph5SelRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (graphset.Graphs.Count >= 5)
            {
                graphselect = 4;
                GraphOffsetTrackBar.Value = graphset.Graphs[graphselect].yoffset;
                GraphYScaleTrackBar.Value = (int)(graphset.Graphs[graphselect].yscale * 100);
                graphset.UpdateGraphs();
            }
        }

        private void GraphsetGetControlValuesCallback()
        {
            graphset.editmode = EditModecomboBox.SelectedIndex;
            graphset.editoption = EditOptioncomboBox.SelectedIndex;
            graphset.editperiodextend = (int)PeriodExtendUpDown.Value;
        }

        private void updateGraphCallback()
        {
            if (stopupdatingGraph == false)
            {
                AnDensityUpDown.Value = graphset.Graphs[0].density;
                int index = 0;
                if (Graph1SelRadioButton.Checked) index = 0;
                else
                if (Graph2SelRadioButton.Checked) index = 1;
                else
                if (Graph3SelRadioButton.Checked) index = 2;
                else
                if (Graph4SelRadioButton.Checked) index = 3;
                else
                if (Graph5SelRadioButton.Checked) index = 4;

                graphset.Graphs[index].changed = true;

                graphset.Graphs[graphselect].yscale = (GraphYScaleTrackBar.Value / 100.0f);
                GraphScaleYLabel.Text = (GraphYScaleTrackBar.Value / 100.0f).ToString();
                /*
                foreach ( var gr in graphset.Graphs)
                {
                    gr.density = density;
                }
                AnDensityUpDown.Value = density;
                */
                GraphLengthLabel.Text = string.Format("{0:n0}", graphset.Graphs[0].datalength);
                GraphXOffsetLabel.Text = string.Format("{0:n0}", graphset.Graphs[0].dataoffset);
                int i;
                int centerposition = graphset.Graphs[0].dataoffset;
                //int centerposition = graphset.Graphs[0].dataoffset + (graphset.Graphs[0].datalength / 2);
                if (processing.sectordata2 != null && processing.rxbuftograph != null)
                {
                    for (i = 0; i < processing.rxbuftograph.Length; i++)
                    {
                        if (processing.rxbuftograph[i] > centerposition) 
                            break;

                    }
                    tbreceived.Append("rxbuftograph i "+i+"\r\n");
                    if (i < processing.rxbuftograph.Length - 1)
                    {
                        int rxbufoffset = i;

                        for (i = 0; i < processing.sectordata2.Count; i++)
                        {
                            if (processing.sectordata2[i].rxbufMarkerPositions > rxbufoffset)
                            {
                                break;
                            }
                        }
                        tbreceived.Append("sectordata i " + i + "\r\n");
                        if (i > 1)
                        {
                            MFMData sectordata = processing.sectordata2[i - 1];
                            int sectoroffset = rxbufoffset - sectordata.rxbufMarkerPositions;


                            rxbufOffsetLabel.Text = "T"+sectordata.track.ToString("D3")+" S"+sectordata.sector+" o:"+sectoroffset.ToString();
                        }
                    }
                }
                Undolevelslabel.Text = "Undo levels: " + (graphset.Graphs[0].undo.Count).ToString();
                //graphset.UpdateGraphs();
                scatterplot.UpdateScatterPlot();

            }
        }
        private void updateAnScatterPlot()
        {
            scatterplot.thresholdmin = MinvScrollBar.Value + OffsetvScrollBar1.Value;
            scatterplot.threshold4us = FourvScrollBar.Value + OffsetvScrollBar1.Value;
            scatterplot.threshold6us = SixvScrollBar.Value + OffsetvScrollBar1.Value;
            scatterplot.thresholdmax = EightvScrollBar.Value + OffsetvScrollBar1.Value;

            HistogramhScrollBar1.Maximum = processing.indexrxbuf;
            if (scatterplot.AnScatViewoffset + scatterplot.AnScatViewlargeoffset < 0)
            {
                scatterplot.AnScatViewoffset = 0;
                scatterplot.AnScatViewlargeoffset = 0;
            }
            if (processing.indexrxbuf != 0)
                if (scatterplot.AnScatViewlargeoffset < processing.indexrxbuf)
                    HistogramhScrollBar1.Value = scatterplot.AnScatViewlargeoffset;
            if (processing.indexrxbuf > 0)
                if (MainTabControl.SelectedIndex == 1)
                {
                    int offset = scatterplot.AnScatViewoffset + scatterplot.AnScatViewlargeoffset;
                    int length = scatterplot.AnScatViewlength;
                    if (length < 0) length = 4000;
                    ScatterHisto.DoHistogram(processing.rxbuf, offset, length);
                }
        }
        private void OpenWavefrmbutton_Click_1(object sender, EventArgs e)
        {
            byte[] temp;

            OpenFileDialog loadwave = new OpenFileDialog();
            loadwave.InitialDirectory = subpath + @"\" + outputfilename.Text;
            loadwave.Filter = "wvfrm files (*.wvfrm)|*.wvfrm|wfm files (*.wfm)|*.wfm|All files(*.*)|*.*";
            //Bin files (*.bin)|*.bin|All files (*.*)|*.*

            if (loadwave.ShowDialog() == DialogResult.OK)
            {

                //try
                {
                    string file = loadwave.FileName;
                    string ext = Path.GetExtension(file);
                    string filename = Path.GetFileName(file);
                    textBoxFilesLoaded.AppendText(filename + "\r\n");
                    graphset.filename = filename;
                    // D:\data\Projects\FloppyControl\DiskRecoveries\M003 MusicDisk\ScopeCaptures
                    //string file = @"D:\data\Projects\FloppyControl\DiskRecoveries\M003 MusicDisk\ScopeCaptures\diff4_T02_H1.wfm";
                    reader = new BinaryReader(new FileStream(file, FileMode.Open));

                    //string path1 = Path.GetFileName(file);

                    //textBoxFilesLoaded.Text += path1 + "\r\n";
                    //processing.CurrentFiles += path1 + "\r\n";
                    //outputfilename.Text = path1.Substring(0, path1.IndexOf("_"));

                    if (ext == ".wvfrm")
                    {
                        //reader.BaseStream.Length


                        //int channels = reader.Read()
                        byte channels = reader.ReadByte();
                        int wvfrmlength = reader.ReadInt32();
                        long length = reader.BaseStream.Length;

                        if (channels > 15)
                        {
                            tbreceived.Append("File header error. Too many channels: " + channels + "\r\n");
                            return;
                        }
                        int i;

                        int dataoffset = 0;
                        int datalength = 1000;
                        int density = 23;
                        int flag = 0;

                        if (graphset.Graphs.Count > 0)
                        {

                            if (wvfrmlength == graphset.Graphs[0].data.Length)
                            {
                                dataoffset = graphset.Graphs[0].dataoffset;
                                datalength = graphset.Graphs[0].datalength;
                                density = graphset.Graphs[0].density;
                            }
                            else
                            {
                                dataoffset = 0;
                                datalength = (int)wvfrmlength - 1;
                                density = 23;
                            }

                            flag = 1;
                        }
                        graphset.Graphs.Clear();
                        int cnt = graphset.Graphs.Count;


                        for (i = 0; i < channels - cnt; i++)
                        {
                            graphset.AddGraph(new byte[wvfrmlength]);
                            if (flag == 1)
                            {
                                graphset.Graphs[i].dataoffset = dataoffset;
                                graphset.Graphs[i].datalength = datalength;
                                graphset.Graphs[i].density = density;
                            }
                        }


                        for (i = 0; i < graphset.Graphs.Count; i++)
                        {
                            if (graphset.Graphs[i].data.Length != length || flag == 0)
                            {
                                graphset.Graphs[i].data = new byte[length];
                                if (graphset.Graphs[i].datalength > length)
                                    graphset.Graphs[i].datalength = (int)length - 1;
                                if (graphset.Graphs[i].dataoffset + graphset.Graphs[i].datalength > length)
                                {
                                    graphset.Graphs[i].datalength = (int)length - 1;
                                    graphset.Graphs[i].dataoffset = 0;
                                }
                            }
                        }

                        if (graphset.Graphs[0].undo.Count > 0)
                            graphset.Graphs[0].undo.Clear();


                        var gr = graphset.Graphs;

                        //graphwaveform[2] = new byte[wvfrmlength]; // Create empty waves for storage of result data
                        //graphwaveform[3] = new byte[wvfrmlength];

                        if ((wvfrmlength * channels) < length)
                        {
                            for (i = 0; i < channels; i++)
                            {
                                gr[i].data = reader.ReadBytes(wvfrmlength);
                            }

                            tbreceived.Append(loadwave.FileName + "\r\n");
                            tbreceived.Append(Path.GetFileName(loadwave.FileName) + "\r\n");
                            tbreceived.Append("FileLength: " + reader.BaseStream.Length + "\r\n");

                            reader.Close();
                        }
                        else
                        {
                            tbreceived.Append("Waveform load error: File seems to be too short!\r\n");
                        }

                        if (channels == 3)
                        {
                            int max = 0, min = 255;
                            int offset = (int)DiffOffsetUpDown.Value;
                            for (i = Math.Abs(offset); i < wvfrmlength - (Math.Abs(offset)); i++)
                            {

                                gr[0].data[i] = (byte)(127 + (gr[0].data[i] - gr[1].data[i + offset]) / 2);
                                if (gr[0].data[i] > max) max = gr[0].data[i];
                                if (gr[0].data[i] < min) min = gr[0].data[i];

                                //gr[0].data[i] = (byte)(127 + (gr[0].data[i] - gr[1].data[i + offset]) / 2);
                            }
                            gr[0].yscale = 192.0f / (float)(max - min);
                            GraphYScaleTrackBar.Value = (int)(gr[0].yscale * 100.0f);
                            GraphScaleYLabel.Text = "" + gr[0].yscale;
                        }
                        else
                        {
                            int max = 0, min = 255;
                            int offset = (int)DiffOffsetUpDown.Value;
                            for (i = Math.Abs(offset); i < wvfrmlength - (Math.Abs(offset)); i++)
                            {
                                if (gr[0].data[i] > max) max = gr[0].data[i];
                                if (gr[0].data[i] < min) min = gr[0].data[i];
                            }
                            gr[0].yscale = 192.0f / (float)(max - min);
                            if (graphset.Graphs.Count >= 5)
                                graphset.Graphs[4].yscale = 192.0f / (float)(max - min);
                            GraphYScaleTrackBar.Value = (int)(gr[0].yscale * 100.0f);
                            GraphScaleYLabel.Text = "" + gr[0].yscale;
                        }

                    }
                    else
                    {
                        //reader.BaseStream.Length
                        tbreceived.Append(openFileDialog1.FileName + "\r\n");
                        tbreceived.Append(Path.GetFileName(openFileDialog1.FileName) + "\r\n");
                        tbreceived.Append("FileLength: " + reader.BaseStream.Length + "\r\n");

                        temp = reader.ReadBytes((int)reader.BaseStream.Length);
                        long length = reader.BaseStream.Length;
                        int i;
                        int channels = 4;

                        if (graphset.Graphs.Count < 4)
                            for (i = 0; i < 4; i++)
                                graphset.AddGraph(new byte[length]);


                        int cnt = 0;
                        var gr = graphset.Graphs;
                        int j;
                        for (i = 3200; i < length - channels; i += channels)
                        {
                            for (j = 0; j < channels; j++)
                            {
                                gr[j].data[cnt] = temp[i + j];
                            }
                            cnt++;
                        }
                        reader.Close();
                    }
                }

                CreateGraphs();
                //catch (Exception ex)
                //{
                //    MessageBox.Show("Error: Could not read file from disk. Original error: " + ex.Message);
                //}
            }
        }
        public void GraphFilterButton_Click(object sender, EventArgs e)
        {
            var gr = graphset.Graphs;
            if (gr.Count >= 4)
            {
                int i;
                double val = 0;
                double valadapt = 0;
                double val2 = 0;
                //double RateOfChange = (float)GraphFilterUpDown.Value;
                int diffdist = (int)DiffDistUpDown.Value;
                float diffgain = (float)DiffGainUpDown.Value;
                int diffdist2 = (int)DiffDistUpDown2.Value;
                int length = gr[0].data.Length;

                int diffthreshold = (int)DiffThresholdUpDown.Value;
                int smoothing = (int)SmoothingUpDown.Value;
                int DiffMinDeviation = (int)DiffMinDeviationUpDown.Value;
                int DiffMinDeviation2 = (int)DiffMinDeviation2UpDown.Value;
                int adaptlookahead = (int)AdaptLookAheadUpDown.Value;

                bool adaptivegainenable = AdaptiveGaincheckBox.Checked;
                bool invert = InvertcheckBox.Checked;
                double[] t = new double[length];
                double[] t1 = new double[length];
                double totalmin = 255;
                double totalmax = 0;
                double totalamplitude = 0;

                int smoothingstart = 0 - smoothing;

                //double SignalRatio = (double)SingalRationUpDown.Value;
                double SignalDistRatio = (double)SignalRatioDistUpDown.Value;

                double DCoffset = 0;
                int[] history = new int[smoothing * 2 + 1];
                //int hcnt = 0;
                int total = 0;
                byte[] data = gr[0].data;
                //Smoothing pass
                if (smoothing != 0)
                {
                    for (i = smoothing; i < length - smoothing; i++)
                    {
                        total -= history[i % (smoothing * 2 + 1)]; // subtract oldest value
                        history[i % (smoothing * 2 + 1)] = data[i + smoothing];

                        total += data[i + smoothing];
                        val2 = total / (double)(smoothing * 2.0d);

                        DCoffset += val2;
                        //val = val + (((float)graphwaveform[0][i] - val) / RateOfChange);
                        //t[i] = (byte)((val * 0.4f) + (val2 * 0.6f));

                        if (invert)
                        {
                            t[i] = -(val2);
                        }
                        else
                        {
                            t[i] = val2;
                        }
                        if (i > 5000 && i < (length - smoothing - 5000))
                        {
                            if (totalmax < t[i]) totalmax = t[i];
                            if (totalmin > t[i]) totalmin = t[i];
                        }

                    }


                    // Differential pass
                    if (invert)
                        DCoffset = -DCoffset / (length - (smoothing * 2));
                    else
                        DCoffset = DCoffset / (length - (smoothing * 2));
                    totalmax -= DCoffset;
                    totalmin -= DCoffset;
                    totalamplitude = totalmax - totalmin;
                    tbreceived.Append("Totalmin:" + totalmin + " totalmax:" + totalmax + " totalamp:" + totalamplitude + "\r\n");
                }
                else
                {
                    for (i = 0; i < length; i++)
                    {
                        DCoffset += gr[0].data[i];
                        if (invert)
                        {
                            t[i] = -gr[0].data[i];
                        }
                        else
                        {
                            t[i] = gr[0].data[i];
                        }
                    }
                    // Differential pass
                    if (invert)
                        DCoffset = -DCoffset / length;
                    else
                        DCoffset = DCoffset / length;
                }



                //DCoffset = 0;
                int startdist;
                tbreceived.Append("DC offset:" + DCoffset + "\r\n");
                if (diffdist > diffdist2) startdist = diffdist;
                else startdist = diffdist2;

                double adaptivegain = 1;
                double adaptivegainnew = 0;
                double adaptivegainold = 0;
                double adaptiverateofchange = 0.01;
                double adaptivegainoldtonew = 0;
                double maxvalue = 0;
                double minvalue = 0;

                double[] adaptivegainhistory = new double[length];
                if (diffdist * SignalDistRatio == 0) return;
                for (i = startdist; i < length - adaptlookahead; i++)
                {

                    valadapt = t[i + adaptlookahead] - DCoffset;
                    val = t[i] - DCoffset;
                    if (maxvalue < valadapt) maxvalue = valadapt;
                    if (minvalue > valadapt) minvalue = valadapt;

                    if (i % (int)(diffdist * SignalDistRatio) == 0 && adaptivegainenable)
                    {
                        //adaptivegain = (adaptivegain + ((255-maxvalue)/2))/2.0;

                        //adaptivegain = (adaptivegain+(1.0-((maxvalue-minvalue)/256.0)))/2;
                        adaptivegainold = adaptivegainnew;
                        //adaptivegainnew = (1/((maxvalue - minvalue) / SignalRatio));
                        adaptivegainnew = totalamplitude / (maxvalue - minvalue);
                        //tbreceived.Append(" "+adaptivegainnew);
                        if (adaptivegain < 1.0)
                            adaptivegain = 1.0;
                        if (adaptivegain > 4)
                            adaptivegain = 4;

                        //tbreceived.Append(" i: "+i+" a"+adaptivegain+" mm"+(maxvalue-minvalue));
                        maxvalue = 0;
                        minvalue = 0;
                        adaptivegainoldtonew = 0;
                    }
                    if (adaptivegainenable)
                    {
                        if (adaptivegainoldtonew < 1)
                            adaptivegainoldtonew += adaptiverateofchange;
                        else
                            adaptivegainoldtonew = 1;
                        adaptivegain = adaptivegainold * (1 - adaptivegainoldtonew) + adaptivegainnew * adaptivegainoldtonew;
                        adaptivegainhistory[i] = adaptivegain;
                    }
                    else
                    {
                        adaptivegain = 1;
                        adaptivegainhistory[i] = 1;
                    }

                    val = ((val - (t[i - diffdist] - DCoffset)) * diffgain * adaptivegain);
                    val2 = ((val - (t[i - diffdist2] - DCoffset)) * diffgain * adaptivegain);
                    t1[i] = (128 + ((val * 0.5) + (val2 * 0.5)));

                    if (t1[i] > 255) gr[3].data[i] = 255;
                    else if (t1[i] < 0) gr[3].data[i] = 0;
                    else gr[3].data[i] = (byte)(t1[i]);

                }

                //int hyst = (int)GraphDiffHystUpDown.Value;
                int old = 0;
                int period = 0;

                if (AnReplacerxbufBox.Checked)
                {
                    resetinput();
                    //indexrxbuf = 0;
                    processing.indexrxbuf = 0;
                }

                int fluxdirection = 0;
                int orgDiffMinDeviation = DiffMinDeviation;
                float periodfactor = 2f;
                int periodoffset = -23;
                float rxbuftographlength = (length * (length / 3250000f)) / 13f;
                if (rxbuftographlength < 250000)
                    rxbuftographlength = 1250000;
                processing.rxbuftograph = new int[(int)rxbuftographlength];
                // Zero crossing pass
                for (i = 0; i < length - diffdist; i++)
                {
                    if (fluxdirection == 0) // is the direction upwards?
                    {
                        if (adaptivegainhistory[i + diffdist] >= 2)
                            DiffMinDeviation = DiffMinDeviation2;
                        else
                            DiffMinDeviation = orgDiffMinDeviation;
                        if (t1[i] >= diffthreshold + DiffMinDeviation) // is the signal crossing zero point (unsigned byte zero = 128)
                        {
                            fluxdirection = 1; // Switch checking direction
                            period = i - old; // Calculate period
                            if (period > 10 && period < 120) // Tthis works as the time domain filter
                            {
                                processing.rxbuftograph[processing.indexrxbuf] = i;
                                processing.rxbuf[processing.indexrxbuf++] = (byte)(period * periodfactor + periodoffset);

                                //tbreceived.Append(period + " ");
                                gr[1].data[i] = 200;
                                old = i;
                            }
                            /*else if (period >= 100)
                            {
                                rxbuf[processing.indexrxbuf++] = 120;
                                old = i;
                                byte flip = 0;
                                for (int q = 0; q < 500; q++) 
                                {
                                    if ((q & 1) == 0)
                                        flip = 120;
                                    else flip = 100;
                                    gr[2].data[i + q] = flip;
                                }
                            }*/
                        }
                        else gr[1].data[i] = 20; // No crossing detected
                    }
                    else // is the direction downwards?
                    {
                        if (t1[i] < diffthreshold - DiffMinDeviation)
                        {
                            fluxdirection = 0;
                            period = i - old;
                            if (period > 10 && period < 120)
                            {
                                processing.rxbuftograph[processing.indexrxbuf] = i;
                                processing.rxbuf[processing.indexrxbuf++] = (byte)(period * periodfactor + periodoffset);

                                //tbreceived.Append(period + " ");
                                gr[1].data[i] = 200;
                                old = i;
                            }
                            /*
                            else if (period >= 100)
                            {
                                rxbuf[processing.indexrxbuf++] = 120;
                                old = i;

                                byte flip = 0;
                                for (int q = 0; q < 500; q++)
                                {
                                    if ((q & 1) == 0)
                                        flip = 120;
                                    else flip = 100;
                                    gr[2].data[i + q] = flip;
                                    
                                }
                            }
                            */
                        }
                        else gr[1].data[i] = 20;
                    }
                    period = i - old;
                    if (period > 120)
                    {
                        processing.rxbuftograph[processing.indexrxbuf] = i;
                        processing.rxbuf[processing.indexrxbuf++] = 10;
                        processing.rxbuftograph[processing.indexrxbuf] = i;
                        processing.rxbuf[processing.indexrxbuf++] = 20;
                        //processing.rxbuftograph[processing.indexrxbuf] = i;
                        //processing.rxbuf[processing.indexrxbuf++] = 10;
                        //processing.rxbuftograph[processing.indexrxbuf] = i;
                        //processing.rxbuf[processing.indexrxbuf++] = 10;
                        //processing.rxbuftograph[processing.indexrxbuf] = i;
                        //processing.rxbuf[processing.indexrxbuf++] = 10;
                        old = i;
                        period = 0;
                        //byte flip = 0;

                        //Use graph2 as a marker
                        /*
                        if ( i+500 < gr[2].data.Length )
                            for (int q = 0; q < 500; q++)
                            {
                                if ((q & 1) == 0)
                                    flip = 120;
                                else flip = 100;
                                gr[2].data[i + q] = flip;
                            }
                            */
                    }

                }

                FindPeaks();
                rxbufEndUpDown.Maximum = processing.indexrxbuf;
                rxbufStartUpDown.Maximum = processing.indexrxbuf;

                rxbufEndUpDown.Value = processing.indexrxbuf;
                HistogramhScrollBar1.Minimum = 0;
                HistogramhScrollBar1.Maximum = processing.indexrxbuf;

                graphset.SetAllChanged();

                if (scatterplot.AnScatViewlength == 0 || scatterplot.AnScatViewlength == 100000)
                    scatterplot.AnScatViewlength = processing.indexrxbuf - 1;
                scatterplot.UpdateScatterPlot();
                graphset.UpdateGraphs();
                if (processing.indexrxbuf > 0)
                    ProcessingTab.Enabled = true;

            }
        }

        private void button31_Click_2(object sender, EventArgs e)
        {
            var gr = graphset.Graphs;
            int i;

            int diffdist = (int)DiffDistUpDown.Value;
            float diffgain = (float)DiffGainUpDown.Value;
            int diffdist2 = (int)DiffDistUpDown2.Value;
            int length = gr[0].data.Length;

            int diffthreshold = (int)DiffThresholdUpDown.Value;
            int smoothing = (int)SmoothingUpDown.Value;
            int DiffMinDeviation = (int)DiffMinDeviationUpDown.Value;
            int DiffMinDeviation2 = (int)DiffMinDeviation2UpDown.Value;
            int adaptlookahead = (int)AdaptLookAheadUpDown.Value;

            bool adaptivegainenable = AdaptiveGaincheckBox.Checked;
            bool invert = InvertcheckBox.Checked;
            double[] t = new double[length];
            byte[] t1 = gr[3].data;
            //double totalmin = 255;
            //double totalmax = 0;
            //double totalamplitude = 0;

            int smoothingstart = 0 - smoothing;

            //double SignalRatio = (double)SingalRationUpDown.Value;
            double SignalDistRatio = (double)SignalRatioDistUpDown.Value;

            if (AnReplacerxbufBox.Checked)
            {
                resetinput();
                //indexrxbuf = 0;
                processing.indexrxbuf = 0;
            }

            //double DCoffset = 0;
            int[] history = new int[smoothing * 2 + 1];
            //int hcnt = 0;
            //int total = 0;
            byte[] data = gr[0].data;

            int fluxdirection = 0;
            int orgDiffMinDeviation = DiffMinDeviation;
            float periodfactor = 2f;
            int periodoffset = -23;
            float rxbuftographlength = (length * (length / 3250000f)) / 13f;
            if (rxbuftographlength < 250000)
                rxbuftographlength = 250000;
            processing.rxbuftograph = new int[(int)rxbuftographlength];
            // Zero crossing pass
            int period, old = 0;
            for (i = 0; i < length - diffdist; i++)
            {
                if (fluxdirection == 0) // is the direction upwards?
                {
                    if (t1[i] >= diffthreshold + DiffMinDeviation) // is the signal crossing zero point (unsigned byte zero = 128)
                    {
                        fluxdirection = 1; // Switch checking direction
                        period = i - old; // Calculate period
                        if (period > 10 && period < 120) // Tthis works as the time domain filter
                        {
                            processing.rxbuftograph[processing.indexrxbuf] = i;
                            processing.rxbuf[processing.indexrxbuf++] = (byte)(period * periodfactor + periodoffset);

                            //tbreceived.Append(period + " ");
                            gr[1].data[i] = 200;
                            old = i;
                        }
                        /*else if (period >= 100)
                        {
                            rxbuf[processing.indexrxbuf++] = 120;
                            old = i;
                            byte flip = 0;
                            for (int q = 0; q < 500; q++) 
                            {
                                if ((q & 1) == 0)
                                    flip = 120;
                                else flip = 100;
                                gr[2].data[i + q] = flip;
                            }
                        }*/
                    }
                    else gr[1].data[i] = 20; // No crossing detected
                }
                else // is the direction downwards?
                {
                    if (t1[i] < diffthreshold - DiffMinDeviation)
                    {
                        fluxdirection = 0;
                        period = i - old;
                        if (period > 10 && period < 120)
                        {
                            processing.rxbuftograph[processing.indexrxbuf] = i;
                            processing.rxbuf[processing.indexrxbuf++] = (byte)(period * periodfactor + periodoffset);

                            //tbreceived.Append(period + " ");
                            gr[1].data[i] = 200;
                            old = i;
                        }
                        /*
                        else if (period >= 100)
                        {
                            rxbuf[processing.indexrxbuf++] = 120;
                            old = i;

                            byte flip = 0;
                            for (int q = 0; q < 500; q++)
                            {
                                if ((q & 1) == 0)
                                    flip = 120;
                                else flip = 100;
                                gr[2].data[i + q] = flip;

                            }
                        }
                        */
                    }
                    else gr[1].data[i] = 20;
                }
                period = i - old;
                if (period > 120)
                {
                    processing.rxbuftograph[processing.indexrxbuf] = i;
                    processing.rxbuf[processing.indexrxbuf++] = 10;
                    processing.rxbuftograph[processing.indexrxbuf] = i;
                    processing.rxbuf[processing.indexrxbuf++] = 20;
                    //processing.rxbuftograph[processing.indexrxbuf] = i;
                    //processing.rxbuf[processing.indexrxbuf++] = 10;
                    //processing.rxbuftograph[processing.indexrxbuf] = i;
                    //processing.rxbuf[processing.indexrxbuf++] = 10;
                    //processing.rxbuftograph[processing.indexrxbuf] = i;
                    //processing.rxbuf[processing.indexrxbuf++] = 10;
                    old = i;
                    period = 0;
                    //byte flip = 0;

                    //Use graph2 as a marker
                    /*
                    if ( i+500 < gr[2].data.Length )
                        for (int q = 0; q < 500; q++)
                        {
                            if ((q & 1) == 0)
                                flip = 120;
                            else flip = 100;
                            gr[2].data[i + q] = flip;
                        }
                        */
                }

            }


            rxbufEndUpDown.Maximum = processing.indexrxbuf;
            rxbufStartUpDown.Maximum = processing.indexrxbuf;

            rxbufEndUpDown.Value = processing.indexrxbuf;
            HistogramhScrollBar1.Minimum = 0;
            HistogramhScrollBar1.Maximum = processing.indexrxbuf;
            //processing.indexrxbuf = indexrxbuf;

            graphset.SetAllChanged();

            if (scatterplot.AnScatViewlength == 0)
                scatterplot.AnScatViewlength = processing.indexrxbuf - 1;
            scatterplot.UpdateScatterPlot();
            graphset.UpdateGraphs();
            if (processing.indexrxbuf > 0)
                ProcessingTab.Enabled = true;
        }
        
        private void DiffDistUpDown_ValueChanged(object sender, EventArgs e)
        {
            GraphFilterButton.PerformClick();
        }

        private void DiffGainUpDown_ValueChanged(object sender, EventArgs e)
        {
            GraphFilterButton.PerformClick();
        }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            GraphFilterButton.PerformClick();
        }

        private void DiffDistUpDown2_ValueChanged(object sender, EventArgs e)
        {
            GraphFilterButton.PerformClick();
        }

        // Process the read data signal captured using the scope
        private void button19_Click(object sender, EventArgs e)
        {
            int i;
            //double val = 0;
            //double val2 = 0;
            //double RateOfChange = (float)GraphFilterUpDown.Value;
            int diffdist = (int)DiffDistUpDown.Value;
            float diffgain = (float)DiffGainUpDown.Value;
            int diffdist2 = (int)DiffDistUpDown2.Value;
            if (graphwaveform[2] == null) return;
            int length = graphwaveform[2].Length;

            int diffthreshold = (int)DiffThresholdUpDown.Value;

            // The captured data has the high and low at 119 and 108
            float[] t = new float[graphwaveform[0].Length];
            //indexrxbuf = 0;
            processing.indexrxbuf = 0;
            int j;
            int old = 0;
            int period;
            //Smoothing pass
            for (i = 0; i < length; i++)
            {
                if (graphwaveform[2][i] < 113)
                {
                    period = i - old;
                    processing.rxbuf[processing.indexrxbuf++] = (byte)period;
                    old = i;
                    for (j = 0; j < 100; j++) // skip to end of pulse
                    {
                        if (graphwaveform[2][i] > 113)
                        {
                            break;
                        }
                        if (i < length - 1)
                            i++;
                        else break;
                    }
                }

            }

            rxbufEndUpDown.Maximum = processing.indexrxbuf;
            rxbufStartUpDown.Maximum = processing.indexrxbuf;

            rxbufEndUpDown.Value = processing.indexrxbuf;
            HistogramhScrollBar1.Minimum = 0;
            HistogramhScrollBar1.Maximum = processing.indexrxbuf;
            graphset.SetAllChanged();

            graphset.UpdateGraphs();
        }
        
        
        private void CaptureDataBtn_Click(object sender, EventArgs e)
        {
            int i;

            scope.tbr = tbreceived;

            string connection = (string)Properties.Settings.Default["ScopeConnection"];
            scope.Connect(connection);
            for (i = 0; i < 20; i++)
            {
                Thread.Sleep(50);
                if (scope.connectionStatus == 1)
                    break;
            }
            if (i == 19)
            {
                tbreceived.Append("Connection failed\r\n");
            }
            else
            {
                controlfloppy.binfilecount = binfilecount;
                controlfloppy.DirectStep = DirectStepCheckBox.Checked;
                controlfloppy.MicrostepsPerTrack = (int)MicrostepsPerTrackUpDown.Value;
                controlfloppy.trk00offset = (int)TRK00OffsetUpDown.Value;
                controlfloppy.EndTrack = (int)EndTracksUpDown.Value;
                controlfloppy.StartTrack = (int)StartTrackUpDown.Value;
                controlfloppy.tbr = tbreceived;
                //processing.indexrxbuf            = indexrxbuf;
                controlfloppy.StepStickMicrostepping = (int)Properties.Settings.Default["MicroStepping"];
                controlfloppy.outputfilename = outputfilename.Text;
                controlfloppy.rxbuf = processing.rxbuf;

                selectedBaudRate = (int)Properties.Settings.Default["DefaultBaud"];
                selectedPortName = (string)Properties.Settings.Default["DefaultPort"];
                scope.serialPort1.PortName = selectedPortName;
                scope.serialPort1.BaudRate = selectedBaudRate;
                scope.ScopeMemDepth = (int)NumberOfPointsUpDown.Value;
                scope.UseAveraging = NetworkUseAveragingCheckBox.Checked;

                controlfloppy.binfilecount = binfilecount;
                controlfloppy.DirectStep = DirectStepCheckBox.Checked;
                controlfloppy.MicrostepsPerTrack = (int)MicrostepsPerTrackUpDown.Value;
                controlfloppy.StepStickMicrostepping = (int)Properties.Settings.Default["StepStickMicrostepping"];
                controlfloppy.trk00offset = (int)TRK00OffsetUpDown.Value;
                controlfloppy.EndTrack = (int)NetworkCaptureTrackEndUpDown.Value;

                controlfloppy.tbr = tbreceived;
                //processing.indexrxbuf = indexrxbuf;

                controlfloppy.outputfilename = outputfilename.Text;
                controlfloppy.rxbuf = processing.rxbuf;

                // Callbacks
                controlfloppy.updateHistoAndSliders = updateHistoAndSliders;
                controlfloppy.ControlFloppyScatterplotCallback = ControlFloppyScatterplotCallback;
                controlfloppy.Setrxbufcontrol = Setrxbufcontrol;


                scope.controlfloppy = controlfloppy; // reference the controlfloppy class

                if (NetCaptureRangecheckBox.Checked)
                {
                    int start, end;

                    start = (int)NetworkCaptureTrackStartUpDown.Value;
                    end = (int)NetworkCaptureTrackEndUpDown.Value;

                    for (i = start; i < end + 1; i++)
                    {
                        controlfloppy.EndTrack = i;
                        controlfloppy.StartTrack = i;

                        scope.capturedataindex = 0;
                        scope.capturedatablocklength = 250000;
                        scope.stop = 0;
                        scope.capturedatastate = 0;
                        scope.xscalemv = (int)xscalemvUpDown.Value;
                        scope.capturetimerstart();
                        while (scope.SaveFinished == false && processing.stop != 1)
                        {
                            Thread.Sleep(100);
                            Application.DoEvents();
                        }
                        scope.SaveFinished = false;
                        if (processing.stop == 1)
                            break;
                    }
                }
                else if (NetworkDoAllBad.Checked)
                {
                    int start, end;

                    start = (int)NetworkCaptureTrackStartUpDown.Value;
                    end = (int)NetworkCaptureTrackEndUpDown.Value;

                    int j, dotrack = 0;

                    for (i = start; i < end + 1; i++)
                    {
                        for (j = 0; j < processing.sectorspertrack; j++)
                            if (processing.sectormap.sectorok[i, j] == SectorMapStatus.empty || processing.sectormap.sectorok[i, j] == SectorMapStatus.HeadOkDataBad)
                            {
                                dotrack = 1;
                                break;
                            }
                            else
                            {
                                dotrack = 0;
                            }
                        if (dotrack == 1)
                        {
                            controlfloppy.StartTrack = i;

                            scope.capturedataindex = 0;
                            scope.capturedatablocklength = 250000;
                            scope.stop = 0;
                            scope.capturedatastate = 0;
                            scope.capturetimerstart();
                            while (scope.SaveFinished == false && processing.stop != 1)
                            {
                                Thread.Sleep(100);
                                Application.DoEvents();
                            }
                        }
                        scope.SaveFinished = false;
                        if (processing.stop == 1)
                            break;
                    }
                }
                else
                {
                    controlfloppy.StartTrack = (int)NetworkCaptureTrackStartUpDown.Value;

                    scope.capturedataindex = 0;
                    scope.capturedatablocklength = 250000;
                    scope.stop = 0;
                    scope.capturedatastate = 0;
                    scope.capturetimerstart();
                }
            }
        }

        private void button29_Click(object sender, EventArgs e)
        {
            scope.stop = 1;
            scope.capturedatastate = 3;
            scope.networktimerstop();
            scope.capturetimerstop();
        }
        

        private void CaptureClassbutton_Click(object sender, EventArgs e)
        {
            CaptureTracks();
        }

        private void CaptureTracks()
        {
            resetinput();
            processing.entropy = null;
            tabControl1.SelectedTab = ScatterPlottabPage;
            controlfloppy.MicrostepsPerTrack = (int)MicrostepsPerTrackUpDown.Value;
            controlfloppy.StepStickMicrostepping = (int)Properties.Settings.Default["StepStickMicrostepping"];
            controlfloppy.trk00offset = (int)TRK00OffsetUpDown.Value;
            controlfloppy.EndTrack = (int)EndTracksUpDown.Value;
            controlfloppy.StartTrack = (int)StartTrackUpDown.Value;
            if (controlfloppy.EndTrack == controlfloppy.StartTrack)
                controlfloppy.EndTrack++;
            controlfloppy.TrackDuration = (int)TrackDurationUpDown.Value;
            controlfloppy.outputfilename = outputfilename.Text;

            if (controlfloppy.serialPort1.IsOpen)
                controlfloppy.StartCapture();
            else
                tbreceived.Append("Not connected.\r\n");
        }

        public void Setrxbufcontrol()
        {
            //indexrxbuf = processing.indexrxbuf;
            rxbufStartUpDown.Maximum = processing.rxbuf.Length;
            rxbufEndUpDown.Maximum = processing.rxbuf.Length;
            rxbufEndUpDown.Value = processing.rxbuf.Length;
            HistogramhScrollBar1.Minimum = 0;
            HistogramhScrollBar1.Maximum = processing.indexrxbuf;
            scatterplot.rxbuf = processing.rxbuf;
        }

        private void HistogramhScrollBar1_Scroll(object sender, EventArgs e)
        {
            scatterplot.AnScatViewlargeoffset = HistogramhScrollBar1.Value;
            //tbreceived.Append("AnScatViewoffset: " + AnScatViewoffset+"\r\n");
            //createhistogram();

            scatterplot.UpdateScatterPlot();
        }

        private void GCbutton_Click(object sender, EventArgs e)
        {
            GC.Collect();
        }

        private void AnalysisTab2_Enter_1(object sender, EventArgs e)
        {
            graphset.allowrepaint = false;
        }

        private void rtbSectorMap_DoubleClick(object sender, EventArgs e)
        {
            rtbSectorMap.DeselectAll();
            RateOfChange2UpDown.Focus();
            Application.DoEvents();
            processing.sectormap.RefreshSectorMap();
        }

        private void button18_Click(object sender, EventArgs e)
        {
            int i;
            int offset = (int)DiffOffsetUpDown.Value;
            for (i = Math.Abs(offset); i < graphwaveform[0].Length - (Math.Abs(offset)); i++)
            {
                graphwaveform[2][i] = (byte)(127 + (graphwaveform[0][i] - graphwaveform[1][i + offset]) / 2);
            }
        }

        private void DiffOffsetUpDown_ValueChanged(object sender, EventArgs e)
        {
            if (graphwaveform[0] != null)
            {
                int i;
                int offset = (int)DiffOffsetUpDown.Value;
                for (i = Math.Abs(offset); i < graphwaveform[0].Length - (Math.Abs(offset)); i++)
                {
                    graphwaveform[2][i] = (byte)(127 + (graphwaveform[0][i] - graphwaveform[1][i + offset]) / 2);
                }
                graphset.Graphs[2].changed = true;
                graphset.UpdateGraphs();
            }
        }

        private void FloppyControl_SizeChanged(object sender, EventArgs e)
        {
            int i;

            for (i = 0; i < graphset.Graphs.Count; i++)
            {
                graphset.Graphs[i].width = GraphPictureBox.Width;
                graphset.Graphs[i].height = GraphPictureBox.Height;
                graphset.Resize();
            }
        }

        // Undo
        private void button31_Click_1(object sender, EventArgs e)
        {
            int i;
            if (graphset.Graphs.Count > 1)
                if (graphset.Graphs[0].undo.Count > 0)
                {
                    var undo = graphset.Graphs[0].undo;
                    int undolistindex = undo.Count - 1;
                    int offset = undo[undolistindex].offset;
                    byte[] d = undo[undolistindex].undodata;
                    int length = d.Length;

                    for (i = 0; i < length; i++)
                    {
                        graphset.Graphs[0].data[i + offset] = d[i];
                    }
                    undo.Remove(undo[undolistindex]);

                    graphset.Graphs[0].changed = true;
                    graphset.UpdateGraphs();
                }
        }

        private void SaveWaveformButton_Click(object sender, EventArgs e)
        {
            graphset.saveAll();
        }

        //Copy graph[0]
        private void button32_Click(object sender, EventArgs e)
        {
            Graph2 src = graphset.Graphs[0];

            if (graphset.Graphs.Count < 5)
                graphset.AddGraph((byte[])src.data.Clone());

            Graph2 dst = graphset.Graphs[4];

            dst.changed = true;
            dst.data = Clone4(src.data);
            dst.datalength = src.datalength;
            dst.dataoffset = src.dataoffset;
            dst.density = src.density;

            dst.yscale = src.yscale;
            dst.yoffset = src.yoffset;
            src.zorder = 10;
            dst.zorder = 9;
            graphset.UpdateGraphs();
        }

        static byte[] Clone4(byte[] array)
        {
            byte[] result = new byte[array.Length];
            Buffer.BlockCopy(array, 0, result, 0, array.Length * sizeof(byte));
            return result;
        }

        private void button33_Click(object sender, EventArgs e)
        {
            graphset.Graphs[graphselect].DCOffset();
            graphset.Graphs[graphselect].changed = true;
            graphset.UpdateGraphs();
        }

        private void Lowpassbutton_Click(object sender, EventArgs e)
        {
            graphset.Graphs[graphselect].Lowpass((int)SmoothingUpDown.Value);
            graphset.Graphs[graphselect].changed = true;
            graphset.UpdateGraphs();
        }
        private void button3_Click(object sender, EventArgs e)
        {
            graphset.Graphs[graphselect].Lowpass2((int)SmoothingUpDown.Value);
            graphset.Graphs[graphselect].changed = true;
            graphset.UpdateGraphs();
        }
        private void button33_Click_1(object sender, EventArgs e)
        {
            graphset.Graphs[graphselect].Highpass((int)HighpassThresholdUpDown.Value);
            graphset.Graphs[graphselect].changed = true;
            graphset.UpdateGraphs();
        }

        
        private void button1_Click(object sender, EventArgs e)
        {
            int indexS1, threadid;

            ECSettings ecSettings = new ECSettings();
            ECResult sectorresult;
            ecSettings.sectortextbox = textBoxSector;

            if (BadSectorListBox.SelectedIndices.Count >= 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                ecSettings.indexS1 = indexS1;
                ecSettings.periodSelectionStart = (int)ScatterMinUpDown.Value;
                ecSettings.periodSelectionEnd = (int)ScatterMaxUpDown.Value;
                ecSettings.combinations = (int)CombinationsUpDown.Value;
                ecSettings.threadid = threadid;
                ecSettings.C6Start = (int)C6StartUpDown.Value;
                ecSettings.C8Start = (int)C8StartUpDown.Value;
            }
            else
            {
                textBoxReceived.AppendText("Error, no data selected.");
                return;
            }
            if (processing.procsettings.platform == 0)
            {
                processing.ECCluster2(ecSettings);
            }
            else processing.ProcessClusterAmiga(ecSettings);
        }

        private void FloppyControl_Paint(object sender, PaintEventArgs e)
        {

        }

        private void ScatterPlottabPage_Paint(object sender, PaintEventArgs e)
        {
            scatterplot.UpdateScatterPlot();
        }

        private void AdaptiveScan()
        {
            float i;

            for (i = 0.6f; i < 2f; i += 0.2f)
            {
                if (processing.stop == 1)
                    break;
                RateOfChangeUpDown.Value = (decimal)i;
                Application.DoEvents();
                if (processing.procsettings.platform == 0)
                    ProcessPC();
                else
                    ProcessAmiga();
            }
            //processing.sectormap.RefreshSectorMap();
        }

        private void DirectStepCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default["DirectStep"] = DirectStepCheckBox.Checked;
            Properties.Settings.Default.Save();
            controlfloppy.DirectStep = DirectStepCheckBox.Checked;
        }

        private void MicrostepsPerTrackUpDown_ValueChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default["Microstepping"] = (int)MicrostepsPerTrackUpDown.Value;
            Properties.Settings.Default.Save();
        }

        private void SignalRatioDistUpDown_ValueChanged(object sender, EventArgs e)
        {
            if (AnAutoUpdateCheckBox.Checked)
                GraphFilterButton.PerformClick();
        }

        private void AdaptLookAheadUpDown_ValueChanged(object sender, EventArgs e)
        {
            if (AnAutoUpdateCheckBox.Checked)
                GraphFilterButton.PerformClick();
        }

        private void DiffMinDeviationUpDown_ValueChanged(object sender, EventArgs e)
        {
            if (AnAutoUpdateCheckBox.Checked)
                GraphFilterButton.PerformClick();
        }

        private void DiffMinDeviation2UpDown_ValueChanged(object sender, EventArgs e)
        {
            if (AnAutoUpdateCheckBox.Checked)
                GraphFilterButton.PerformClick();
        }
        

        // Waveform editor tab, fix 8us method, an attempt to find and fix 8us waveform distortions
        private void button34_Click(object sender, EventArgs e)
        {
            int i;
            byte[] d = graphset.Graphs[0].data;
            byte[] g3 = graphset.Graphs[2].data;
            byte[] g4 = graphset.Graphs[3].data;
            int diff;
            int diffdist = (int)DiffTest2UpDown.Value;
            int start = graphset.Graphs[0].dataoffset;
            int length = graphset.Graphs[0].datalength;
            int threshold = (int)DiffTestUpDown.Value;
            int thresholddev = (int)DiffTestUpDown.Value;
            int thresholdtest = (int)ThresholdTestUpDown.Value;
            //for (i = diffdist; i < d.Length-diffdist; i++)
            int skip = 0;
            int iold = start + diffdist;
            int min = 255;
            int max = 0;
            int amplitude = 0;
            int amplitudeavgcnt = 0;
            int amplitudeavg = 0;
            int before = 0;
            int after = 0;
            int zerocrossingfilter = 0;
            int zerocrossingbeforeafter = 0;
            int zerocrossingdistance = 15;

            ZeroCrossingData[] zc = new ZeroCrossingData[300000];

            //graphset.Graphs[0].Lowpass2(3);
            //if( graphset.Graphs.Count > 5)
            //    graphset.Graphs[4].Lowpass2(3);

            int zcindex = 0;
            //int lengthamplitude = (start + length - diffdist) - (start + diffdist);
            for (i = start + diffdist; i < start + length - diffdist; i++)
            {
                diff = d[i] - d[i - diffdist];
                diff = (diff + (d[i] - d[i - diffdist * 4])) / 2;
                diff = (diff + (d[i] - d[i - diffdist * 3])) / 2;
                diff = (diff + (d[i] - d[i - diffdist * 2])) / 2;
                if (i > start + diffdist + zerocrossingdistance)
                    before -= d[i - zerocrossingdistance];
                before += d[i];

                if (i > start + diffdist + zerocrossingdistance)
                    after -= d[i];
                after += d[i + zerocrossingdistance];


                g4[i] = (byte)(diff + 127);
                skip++;
                zerocrossingfilter++;
                g3[i] = 110;
                if (i % 500 == 0)
                {
                    amplitude = max - min;
                    amplitudeavgcnt += amplitude;


                    if ((i - (start + diffdist)) > 0)
                        amplitudeavg = amplitudeavgcnt / 500;
                    threshold = amplitudeavg / 2 - thresholddev;
                    amplitudeavgcnt = 0;
                    min = 255;
                    max = 0;
                }
                if (d[i + 500] > max) max = d[i + 500];
                if (d[i + 500] < min) min = d[i + 500];

                amplitude = max - min;
                amplitudeavgcnt += amplitude;

                // Zero crossing
                // Going positive
                if (d[i - 1] < 128 && d[i] >= 128 && zerocrossingfilter > 30)
                {
                    zerocrossingbeforeafter = (after / zerocrossingdistance - 127) - (before / zerocrossingdistance - 127);
                    tbreceived.Append("Pos Zero crossing: i " + i + " before: " + (before / zerocrossingdistance - 127) +
                        " after: " + (after / zerocrossingdistance - 127) + " B/A: " + zerocrossingbeforeafter + "\r\n");
                    zc[zcindex] = new ZeroCrossingData();
                    zc[zcindex].after = after / zerocrossingdistance - 127;
                    zc[zcindex].before = before / zerocrossingdistance - 127;
                    zc[zcindex].negpos = 1;
                    zc[zcindex].zcbeforeafter = zerocrossingbeforeafter;
                    zc[zcindex].index = i;
                    zcindex++;

                    if (zerocrossingbeforeafter < thresholdtest)
                    {
                        g3[i] = 85;
                        zerocrossingfilter = 15;
                    }
                    else
                    {
                        g3[i] = 90;
                        zerocrossingfilter = 0;
                    }

                }
                // Going negative
                if (d[i - 1] >= 126 && d[i] < 126 && zerocrossingfilter > 30)
                {
                    zerocrossingbeforeafter = (before / zerocrossingdistance - 127) - (after / zerocrossingdistance - 127);
                    tbreceived.Append("Neg Zero crossing: i " + i + " before: " + (before / zerocrossingdistance - 127) +
                        " after: " + (after / zerocrossingdistance - 127) + " B/A: " + zerocrossingbeforeafter + "\r\n");
                    zc[zcindex] = new ZeroCrossingData();
                    zc[zcindex].after = after / zerocrossingdistance - 127;
                    zc[zcindex].before = before / zerocrossingdistance - 127;
                    zc[zcindex].negpos = 0;
                    zc[zcindex].zcbeforeafter = zerocrossingbeforeafter;
                    zc[zcindex].index = i;
                    zcindex++;

                    if (zerocrossingbeforeafter < thresholdtest)
                    {
                        g3[i] = 80;
                        zerocrossingfilter = 15;
                    }
                    else
                    {
                        g3[i] = 90;
                        zerocrossingfilter = 0;
                    }
                }

                /*
                // Differential check
                if (diff > threshold && skip > 30)
                {
                    tbreceived.Append(" i " + i + " " + diff+" period "+(i-iold)+" amplitude: "+threshold+"\r\n");
                    skip = 0;
                    iold = i;
                    g3[i] = 100;
                }
                if (diff < 0-threshold && skip > 30)
                {
                    tbreceived.Append(" i " + i + " " + diff + " period " + (i - iold) + " amplitude: " + threshold + "\r\n");
                    skip = 0;
                    iold = i;
                    g3[i] = 100;
                }
                */
            }
            /*
            int j;
            
            for (i = 1; i < zcindex-1; i++)
            {
                if (zc[i].zcbeforeafter < thresholdtest)
                {
                    if( zc[i-1].zcbeforeafter > thresholdtest && zc[i + 1].zcbeforeafter > thresholdtest)
                    if (zc[i].before < 11 && zc[i].after < 11)
                        if (zc[i + 1].index - zc[i - 1].index > 70 && zc[i + 1].index - zc[i - 1].index < 90)
                        {
                            for (j = 0; j < 50; j++)
                            {
                                if (zc[i-1].negpos == 0)
                                    d[zc[i].index + j - 25] = 127 - 10;
                                else
                                    d[zc[i-1].index + j - 25] = 127 + 10;
                            }
                        }
                }
            }
            */
            graphset.SetAllChanged();
            graphset.UpdateGraphs();
        }

        // Capture data current track button. Captures the track using the scope on the track that's last used when capturing.
        // It must still be connected on the Capture tab.
        private void button35_Click(object sender, EventArgs e)
        {
            int i;

            scope.tbr = tbreceived;

            string connection = (string)Properties.Settings.Default["ScopeConnection"];
            scope.Connect(connection);
            for (i = 0; i < 20; i++)
            {
                Thread.Sleep(50);
                if (scope.connectionStatus == 1)
                    break;
            }
            if (i == 19)
            {
                tbreceived.Append("Connection failed\r\n");
            }
            else
            {
                //scope.serialPort1 = serialPort1; // floppycontrol hardware
                selectedBaudRate = (int)Properties.Settings.Default["DefaultBaud"];
                selectedPortName = (string)Properties.Settings.Default["DefaultPort"];
                scope.serialPort1.PortName = selectedPortName;
                scope.serialPort1.BaudRate = selectedBaudRate;
                scope.ScopeMemDepth = (int)NumberOfPointsUpDown.Value;
                scope.UseAveraging = NetworkUseAveragingCheckBox.Checked;
                scope.xscalemv = (int)xscalemvUpDown.Value;

                controlfloppy.binfilecount = binfilecount;
                controlfloppy.DirectStep = DirectStepCheckBox.Checked;
                controlfloppy.MicrostepsPerTrack = (int)MicrostepsPerTrackUpDown.Value;
                controlfloppy.trk00offset = (int)TRK00OffsetUpDown.Value;
                controlfloppy.EndTrack = (int)NetworkCaptureTrackEndUpDown.Value;

                controlfloppy.tbr = tbreceived;
                //processing.indexrxbuf = indexrxbuf;
                controlfloppy.StepStickMicrostepping = (int)Properties.Settings.Default["StepStickMicrostepping"];
                controlfloppy.outputfilename = outputfilename.Text;
                controlfloppy.rxbuf = processing.rxbuf;
                scope.controlfloppy = controlfloppy; // reference the controlfloppy class

                int start, end;

                start = (int)NetworkCaptureTrackStartUpDown.Value;
                end = (int)NetworkCaptureTrackEndUpDown.Value;

                controlfloppy.StartTrack = i;

                scope.capturedataindex = 0;
                scope.capturedatablocklength = 250000;
                scope.stop = 0;
                scope.capturedatastate = 0;
                scope.NoControlFloppy = true;
                scope.capturetimerstart();

                while (scope.SaveFinished == false && processing.stop != 1)
                {
                    Thread.Sleep(100);
                    Application.DoEvents();
                }
                scope.SaveFinished = false;

            }
        }

        private void FindPeaks()
        {
            if (processing.indexrxbuf == 0) return;
            processing.FindPeaks(HistogramhScrollBar1.Value);

            int peak1 = processing.peak1;
            int peak2 = processing.peak2;
            int peak3 = processing.peak3;
            ProcessingType procmode = ProcessingType.adaptive1;
            if (ProcessingModeComboBox.SelectedItem.ToString() != "")
                procmode = (ProcessingType)Enum.Parse(typeof(ProcessingType), ProcessingModeComboBox.SelectedItem.ToString(), true);
            tbreceived.Append("Selected: " + procmode.ToString() + "\r\n");

            switch (procmode)
            {
                case ProcessingType.normal:
                    FourvScrollBar.Value = peak1 + ((peak2 - peak1) / 2);
                    SixvScrollBar.Value = peak2 + ((peak3 - peak2) / 2);
                    break;
                case ProcessingType.aufit:
                    break;
                case ProcessingType.adaptive1:
                case ProcessingType.adaptive2:
                case ProcessingType.adaptive3:
                case ProcessingType.adaptivePredict:
                    FourvScrollBar.Value = peak1+4;
                    SixvScrollBar.Value = peak2+2;
                    EightvScrollBar.Value = peak3;
                    break;
            }

            /*
            if (AdaptradioButton.Checked)
            {
                FourvScrollBar.Value = peak1;
                SixvScrollBar.Value = peak2;
                EightvScrollBar.Value = peak3;
            }
            else if (NormalradioButton.Checked)
            {
                FourvScrollBar.Value = peak1 + ((peak2 - peak1) / 2);
                SixvScrollBar.Value = peak2 + ((peak3 - peak2) / 2);
                //EightvScrollBar.Value = peak3;
            }
            */
            updateSliderLabels();
        }
        
        private void Histogrampanel1_Click(object sender, EventArgs e)
        {
            FindPeaks();
            updateAnScatterPlot();
        }

        private void JumpTocomboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            int grphcnt = graphset.Graphs.Count;
            int i;
            int index = JumpTocomboBox.SelectedIndex;
            ComboboxItem item;
            item = (ComboboxItem)JumpTocomboBox.Items[index];

            int id = item.id;
            //tbreceived.Append("Item: " + id+"\r\n");


            // First position the scatterplot on the selected area
            int offset = 0;
            for (i = 1; i < processing.sectordata2.Count; i++)
            {
                if (processing.sectordata2[i].rxbufMarkerPositions > scatterplot.rxbufclickindex)
                {
                    offset = scatterplot.rxbufclickindex - processing.sectordata2[i - 1].rxbufMarkerPositions;
                    break;
                }
            }
            //ScatterMinTrackBar.Value = offset;
            //ScatterMaxTrackBar.Value = offset + 14;
            //updateECInterface();

            int scatoffset = processing.sectordata2[id].rxbufMarkerPositions + (int)ScatterMinTrackBar.Value + (int)ScatterOffsetTrackBar.Value;
            int scatlength = processing.sectordata2[id].rxbufMarkerPositions + (int)ScatterMaxTrackBar.Value + (int)ScatterOffsetTrackBar.Value - scatoffset;
            int graphoffset = scatoffset + (scatlength / 2);
            scatterplot.AnScatViewlargeoffset = scatoffset;
            scatterplot.AnScatViewoffset = 0;
            scatterplot.AnScatViewlength = scatlength;
            scatterplot.UpdateScatterPlot();

            if (grphcnt == 0)
            {
                return;
            }
            for (i = 0; i < grphcnt; i++)
            {
                graphset.Graphs[i].datalength = 2000;
                graphset.Graphs[i].dataoffset = processing.rxbuftograph[graphoffset] - 1000;

                if (graphset.Graphs[i].dataoffset < 0)
                    graphset.Graphs[i].dataoffset = 0;

                graphset.Graphs[i].changed = true;
                graphset.Graphs[i].density = 1;
            }
            //tbreceived.Append("rxbuf pos: "+ (processing.sectordata2[id].rxbufMarkerPositions + offset + 1000));
            graphset.UpdateGraphs();
            MainTabControl.SelectedTab = AnalysisTab2;
        }

        private void rtbSectorMap_MouseDown(object sender, MouseEventArgs e)
        {
            ContextMenuStrip smmenu = new ContextMenuStrip();
            int sector, track;
            int i;
            int div = processing.sectorspertrack + 6;
            LimitToTrackUpDown.Value = track = (rtbSectorMap.SelectionStart / div);
            LimitToSectorUpDown.Value = sector = (rtbSectorMap.SelectionStart % div - 5);

            if (sector < 0) return;
            TrackUpDown.Value = track;
            SectorUpDown.Value = sector;

            if (e.Button == MouseButtons.Left)
            {
                tbreceived.Append("Track: " + track + " sector: " + sector + " div:" + div);
                for (i = 0; i < processing.sectordata2.Count; i++)
                {
                    if (processing.sectordata2 == null) continue;
                    if (processing.sectordata2.Count != 0)
                    {
                        if (processing.sectordata2[i].track == track && processing.sectordata2[i].sector == sector)
                        {
                            //int track1 = track, sector1 = sector;
                            if (processing.sectordata2[i].mfmMarkerStatus == processing.sectormap.sectorok[track, sector])
                            {
                                if (processing.sectordata2.Count-1 > i)
                                {
                                    scatterplot.AnScatViewlargeoffset = processing.sectordata2[i].rxbufMarkerPositions - 50;
                                    scatterplot.AnScatViewoffset = 0;

                                    scatterplot.AnScatViewlength = processing.sectordata2[i + 1].rxbufMarkerPositions - scatterplot.AnScatViewlargeoffset + 100;
                                    //tbreceived.Append("AnScatViewOffset"+ AnScatViewoffset+"\r\n");
                                    scatterplot.UpdateScatterPlot();
                                }
                                break;

                            }
                        }
                    }
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                int index = rtbSectorMap.GetCharIndexFromPosition(new Point(e.X, e.Y));
                tbreceived.Append("Index: " + index + "\r\n");
                div = processing.sectorspertrack + 6;
                LimitToTrackUpDown.Value = track = (index / div);
                LimitToSectorUpDown.Value = sector = (index % div - 5);
                tbreceived.Append("Track: " + track + " sector: " + sector + " div:" + div);
                if (sector < 0) return;

                smmenu.ItemClicked += Smmenu_ItemClicked;
                tbreceived.Append("Track: " + track + "\r\n");

                SectorMapContextMenu[] menudata = new SectorMapContextMenu[10];
                ToolStripItem[] item = new ToolStripItem[10];
                int menudataindex = 0;
                // Capture tab

                menudata[menudataindex] = new SectorMapContextMenu();
                menudata[menudataindex].sector = sector;
                menudata[menudataindex].track = track;
                menudata[menudataindex].duration = 5000;
                menudata[menudataindex].cmd = 0;
                item[menudataindex] = smmenu.Items.Add("Recapture T" + track.ToString("D3") + " 5 sec", MainTabControlImageList.Images[0]);
                item[menudataindex].Tag = menudata[menudataindex];

                // Capture tab
                menudataindex++;
                menudata[menudataindex] = new SectorMapContextMenu();
                menudata[menudataindex].sector = sector;
                menudata[menudataindex].track = track;
                menudata[menudataindex].duration = 50000;
                menudata[menudataindex].cmd = 0;
                item[menudataindex] = smmenu.Items.Add("Recapture T" + track.ToString("D3") + " 50 sec", MainTabControlImageList.Images[0]);
                item[menudataindex].Tag = menudata[menudataindex];

                //Error correction tab
                menudataindex++;
                menudata[menudataindex] = new SectorMapContextMenu();
                menudata[menudataindex].sector = sector;
                menudata[menudataindex].track = track;
                menudata[menudataindex].cmd = 1;
                item[menudataindex] = smmenu.Items.Add("Error Correct T" + track.ToString("D3") + " S" + sector, MainTabControlImageList.Images[1]);
                item[menudataindex].Tag = menudata[menudataindex];

                //Scope waveform capture
                menudataindex++;
                menudata[menudataindex] = new SectorMapContextMenu();
                menudata[menudataindex].sector = sector;
                menudata[menudataindex].track = track;
                menudata[menudataindex].cmd = 2;
                item[menudataindex] = smmenu.Items.Add("Get waveform T" + track.ToString("D3") + " S" + sector, MainTabControlImageList.Images[2]);
                item[menudataindex].Tag = menudata[menudataindex];

                //Select rxdata
                menudataindex++;
                menudata[menudataindex] = new SectorMapContextMenu();
                menudata[menudataindex].sector = sector;
                menudata[menudataindex].track = track;
                menudata[menudataindex].cmd = 3;
                item[menudataindex] = smmenu.Items.Add("Limit rxdata T" + track.ToString("D3") + " S" + sector, MainTabControlImageList.Images[2]);
                item[menudataindex].Tag = menudata[menudataindex];

                Point ShowHere = new Point(Cursor.Position.X, Cursor.Position.Y + 10);
                smmenu.Show(ShowHere);
            }
        }

        private void Smmenu_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            SectorMapContextMenu menudata = (SectorMapContextMenu)e.ClickedItem.Tag;
            if (menudata.cmd == 0)
            {
                tbreceived.Append("Track: " + menudata.track.ToString("D3") + " S" + menudata.sector + "\r\n");
                MainTabControl.SelectedTab = CaptureTab;
                StartTrackUpDown.Value = menudata.track;
                EndTracksUpDown.Value = menudata.track;
                TrackDurationUpDown.Value = menudata.duration;
            }
            else if (menudata.cmd == 1)
            {
                MainTabControl.SelectedTab = ErrorCorrectionTab;
                Track1UpDown.Value = menudata.track;
                Sector1UpDown.Value = menudata.sector;
                Track2UpDown.Value = menudata.track;
                Sector2UpDown.Value = menudata.sector;
            }
            else if (menudata.cmd == 2)
            {
                MainTabControl.SelectedTab = NetworkTab;
                NetworkCaptureTrackStartUpDown.Value = menudata.track;
                NetworkCaptureTrackEndUpDown.Value = menudata.track;
            }
            else if (menudata.cmd == 3)
            {
                int i;
                var sd = processing.sectordata2;
                for (i = 0; i < processing.sectordata2.Count; i++)
                {
                    if (sd[i].sector == menudata.sector && sd[i].track == menudata.track && sd[i].mfmMarkerStatus == SectorMapStatus.HeadOkDataBad)
                    {
                        rxbufStartUpDown.Maximum = processing.indexrxbuf;
                        rxbufStartUpDown.Value = sd[i].rxbufMarkerPositions - 100;
                        rxbufEndUpDown.Value = 15000;
                        break;
                    }
                }
            }
        }

        private void button38_Click(object sender, EventArgs e)
        {
            int i;
            tbSectorMap.AppendText("count\tdec\thex\tbin\r\n");
            for (i = 0; i < 256; i++)
            {
                if (mfmbyteenc[i] > 0)
                    tbSectorMap.AppendText(mfmbyteenc[i] + "\t" + i + "\t" + i.ToString("X2") + "\t" + Convert.ToString(i, 2).PadLeft(8, '0') + "\r\n");
                mfmbyteenc[i] = 0;
            }
        }

        private void button36_Click(object sender, EventArgs e)
        {
            int StepStickMicrostepping = 8;
            int i;
            controlfloppy.serialPort1.Write(']'.ToString()); // Full step
            Thread.Sleep(10);

            for (i = 0; i < StepStickMicrostepping - 1; i++)
            {
                controlfloppy.serialPort1.Write('['.ToString()); // Full step
                Thread.Sleep(10);
            }
        }

        private void button39_Click(object sender, EventArgs e)
        {
            controlfloppy.serialPort1.Write('g'.ToString()); // increase track number
            Thread.Sleep(controlfloppy.tracktotrackdelay);
        }

        private void button40_Click(object sender, EventArgs e)
        {
            controlfloppy.serialPort1.Write('t'.ToString()); // increase track number
            Thread.Sleep(controlfloppy.tracktotrackdelay);
        }

        private void button43_Click(object sender, EventArgs e)
        {
            int StepStickMicrostepping = 8;
            int i;
            controlfloppy.serialPort1.Write(']'.ToString()); // Full step
            Thread.Sleep(10);

            for (i = 0; i < StepStickMicrostepping - 1; i++)
            {
                controlfloppy.serialPort1.Write('['.ToString()); // Full step
                Thread.Sleep(10);
            }
        }

        private void button42_Click(object sender, EventArgs e)
        {
            controlfloppy.serialPort1.Write('g'.ToString()); // increase track number
            Thread.Sleep(controlfloppy.tracktotrackdelay);
        }

        private void button41_Click(object sender, EventArgs e)
        {
            controlfloppy.serialPort1.Write('t'.ToString()); // increase track number
            Thread.Sleep(controlfloppy.tracktotrackdelay);
        }

        private void ECMFMByteEncbutton_Click(object sender, EventArgs e)
        {
            int indexS1, threadid;
            ECSettings ecSettings = new ECSettings();
            
            ecSettings.sectortextbox = textBoxSector;

            if (BadSectorListBox.SelectedIndices.Count >= 1)
            {
                indexS1 = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).id;
                threadid = ((badsectorkeyval)BadSectorListBox.Items[BadSectorListBox.SelectedIndices[0]]).threadid;
                ecSettings.indexS1 = indexS1;
                ecSettings.periodSelectionStart = (int)ScatterMinUpDown.Value;
                ecSettings.periodSelectionEnd = (int)ScatterMaxUpDown.Value;
                //ecSettings.combinations = (int)CombinationsUpDown.Value;
                ecSettings.threadid = threadid;
                ecSettings.MFMByteStart = (int)MFMByteStartUpDown.Value;
                ecSettings.MFMByteLength = (int)MFMByteLengthUpDown.Value;
            }
            else
            {
                textBoxReceived.AppendText("Error, no data selected.");
                return;
            }
            if (processing.procsettings.platform == 0)
                processing.ProcessClusterMFMEnc(ecSettings);
            else processing.ProcessClusterAmigaMFMEnc(ecSettings);
        }

        //Iterator test 
        private void button44_Click(object sender, EventArgs e)
        {
            int[] combi = new int[32];
            int[] combilimit = new int[32];
            int i, j, p, q, k;
            int combinations = 0;
            int NumberOfMfmBytes = 3;
            int MaxIndex = 32;
            int iterations;
            for (i = 0; i < NumberOfMfmBytes; i++)
            {
                combilimit[i] = 1;
            }
            for (j = 0; j < MaxIndex; j++)
            {

                for (q = 0; q < NumberOfMfmBytes; q++)
                {
                    combilimit[q]++;

                }

                int temp = combilimit[0];
                iterations = temp;
                for (q = 0; q < NumberOfMfmBytes - 1; q++)
                {
                    iterations *= temp;
                }
                tbreceived.Append("Iterations: " + iterations + "\r\n");

                for (i = 0; i < iterations; i++)
                {
                    //printarray(combi, NumberOfMfmBytes);
                    combi[0]++;
                    for (k = 0; k < NumberOfMfmBytes; k++)
                    {

                        if (combi[k] >= combilimit[0])
                        {
                            combi[k] = 0;
                            combi[k + 1]++;
                        }
                    }

                    combinations++;
                }

            }
            tbreceived.Append("Combinations:" + combinations + "\r\n");
        }

        //Open SCP
        private void button45_Click(object sender, EventArgs e)
        {
            byte[] temp;

            OpenFileDialog loadwave = new OpenFileDialog();
            loadwave.InitialDirectory = subpath + @"\" + outputfilename.Text;
            loadwave.Filter = "scp files (*.scp)|*.scp|All files(*.*)|*.*";
            //Bin files (*.bin)|*.bin|All files (*.*)|*.*

            if (loadwave.ShowDialog() == DialogResult.OK)
            {
                //try
                {
                    string file = loadwave.FileName;
                    string ext = Path.GetExtension(file);
                    string filename = Path.GetFileName(file);
                    textBoxFilesLoaded.AppendText(filename + "\r\n");
                    //graphset.filename = filename;
                    // D:\data\Projects\FloppyControl\DiskRecoveries\M003 MusicDisk\ScopeCaptures
                    //string file = @"D:\data\Projects\FloppyControl\DiskRecoveries\M003 MusicDisk\ScopeCaptures\diff4_T02_H1.wfm";
                    reader = new BinaryReader(new FileStream(file, FileMode.Open));

                    //string path1 = Path.GetFileName(file);

                    //textBoxFilesLoaded.Text += path1 + "\r\n";
                    //processing.CurrentFiles += path1 + "\r\n";
                    //outputfilename.Text = path1.Substring(0, path1.IndexOf("_"));

                    if (ext == ".scp")
                    {
                        //reader.BaseStream.Length
                        long length = reader.BaseStream.Length;

                        temp = reader.ReadBytes((int)length);
                        int i;

                        for (i = 0; i < length - 2; i += 2)
                        {
                            processing.rxbuf[processing.indexrxbuf++] = (byte)((temp[i] << 8 | temp[i + 1]) >> 1);
                            //processing.rxbuf[processing.indexrxbuf++] = (byte)((temp[i] << 8 | temp[i + 1]) - 127);
                        }
                        rxbufEndUpDown.Maximum = processing.indexrxbuf;
                        rxbufEndUpDown.Value = processing.indexrxbuf;
                    }

                    reader.Close();
                    reader.Dispose();
                }
            }
        }

        class TrackSectorOffset
        {
            public int offsetstart { get; set; }
            public int offsetend { get; set; }
            public int length { get; set; }
        }

        private void SaveTrimmedBinFile()
        {
            int i, j;
            string extension = "";
            int ioerror = 0;
            int qq = 0;
            var sectordata2 = processing.sectordata2;

            // First check if there's sectordata2 data available and that all sectors are ok in sectorok array
            int sectorspertrack = processing.diskGeometry[processing.diskformat].sectorsPerTrack;
            int tracksperdisk = processing.diskGeometry[processing.diskformat].tracksPerDisk;
            int sectorsize = processing.diskGeometry[processing.diskformat].sectorSize;
            int numberofheads = processing.diskGeometry[processing.diskformat].numberOfHeads;

            TrackSectorOffset[,] tsoffset = new TrackSectorOffset[200, 20];


            int sectorstotal = sectorspertrack * tracksperdisk * numberofheads;

            byte scpformat;
            int[] tracklengthrxbuf = new int[200];

            int averagetimecompensation;
            ProcessingType procmode = ProcessingType.adaptive1;
            if (ProcessingModeComboBox.SelectedItem.ToString() != "")
                procmode = (ProcessingType)Enum.Parse(typeof(ProcessingType), ProcessingModeComboBox.SelectedItem.ToString(), true);

            if (procmode == ProcessingType.adaptive1 || procmode == ProcessingType.adaptive2 || procmode == ProcessingType.adaptive3)
                averagetimecompensation = ((80 - FourvScrollBar.Value) + (120 - SixvScrollBar.Value) + (160 - EightvScrollBar.Value)) / 3;
            else
                averagetimecompensation = 5;

            // FloppyControl app DiskFormat to SCP format
            byte[] fca2ScpDiskFormat = new byte[] {
                0, // 0 not used
                0x04, // 1 AmigaDOS
                0x04, // 2 DiskSpare
                0x41, // 3 PC DS DD 
                0x43, // 4 PC HD
                0x43, // 5 PC 2M
                0x40, // 6 PC SS DD
                0x04, // DiskSpare 984KB
            };

            scpformat = fca2ScpDiskFormat[(int)processing.diskformat];

            //Checking sectorok data
            int track = 0, sector = 0;
            int trackhead = tracksperdisk * numberofheads;

            i = 0;
            int q = 0;

            // Find all track and sector offsets and lengths
            for (track = 0; track < trackhead; track++)
            {
                for (sector = 0; sector < sectorspertrack; sector++)
                {
                    for (i = 0; i < sectordata2.Count; i++)
                    {
                        if (sectordata2[i].sector == sector && sectordata2[i].track == track)
                        {
                            if (sectordata2[i].mfmMarkerStatus == SectorMapStatus.CrcOk || 
                                sectordata2[i].mfmMarkerStatus == SectorMapStatus.SectorOKButZeroed || 
                                sectordata2[i].mfmMarkerStatus == SectorMapStatus.ErrorCorrected)
                            {
                                TrackSectorOffset tso = new TrackSectorOffset();
                                tso.offsetstart = sectordata2[i].rxbufMarkerPositions;
                                for (q = i + 1; q < sectordata2.Count; q++)
                                {
                                    if (sectordata2[i].mfmMarkerStatus == SectorMapStatus.CrcOk || 
                                        sectordata2[i].mfmMarkerStatus == SectorMapStatus.SectorOKButZeroed || 
                                        sectordata2[i].mfmMarkerStatus == SectorMapStatus.ErrorCorrected)
                                    {

                                        tso.offsetend = sectordata2[q].rxbufMarkerPositions;
                                        //if (track == 80 && sector == 10) tso.offsetend += 100;
                                        break;
                                    }
                                }
                                if (tso.offsetend == 0) tso.offsetend = tso.offsetend = tso.offsetstart + 10000;
                                tso.length = tso.offsetend - tso.offsetstart;
                                if (tso.length > 10000)
                                {
                                    tso.length = 10000;
                                    tso.offsetend = tso.offsetstart + 10000;
                                }
                                tsoffset[track, sector] = tso;
                            }
                        }
                    }
                }
            }
           
            UInt32[] offsettable = new UInt32[200];
            // Write period data to disk in bin format
            extension = "_trimmed.bin";
            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;

            textBoxReceived.AppendText("Path:" + path + "\r\n");

            bool exists = System.IO.Directory.Exists(path);

            while (File.Exists(path))
            {
                binfilecount++;
                path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;
            }

            //Only save if there's any data to save

            //if (processing.diskformat == 3 || processing.diskformat == 4) //PC 720 KB dd or 1440KB hd
            //{
            try
            {
                writer = new BinaryWriter(new FileStream(path, FileMode.Create));
            }
            catch (IOException ex)
            {
                textBoxReceived.AppendText("IO error: " + ex.ToString());
                ioerror = 1;
            }

            for (track = 0; track < trackhead; track++)
            {
                for (sector = 0; sector < sectorspertrack; sector++)
                {
                    if (tsoffset[track, sector] != null) // skip unresolved sectors
                    {
                        writer.Write("T" + track.ToString("D3") + "S" + sector);
                        for (i = tsoffset[track, sector].offsetstart; i < tsoffset[track, sector].offsetend; i++)
                            writer.Write((byte)(processing.rxbuf[i]));
                    }
                }
            }

            tbreceived.Append("Trimmed bin file saved succesfully.\r\n");
            if (writer != null)
            {
                writer.Flush();
                writer.Close();
                writer.Dispose();
            }
        }

        //Save SCP
        private void button46_Click(object sender, EventArgs e)
        {
            int i, j;
            string extension = "";
            int ioerror = 0;
            int qq = 0;
            var sectordata2 = processing.sectordata2;

            // First check if there's sectordata2 data available and that all sectors are ok in sectorok array
            int sectorspertrack = processing.diskGeometry[processing.diskformat].sectorsPerTrack;
            int tracksperdisk = processing.diskGeometry[processing.diskformat].tracksPerDisk;
            int sectorsize = processing.diskGeometry[processing.diskformat].sectorSize;
            int numberofheads = processing.diskGeometry[processing.diskformat].numberOfHeads;

            TrackSectorOffset[,] tsoffset = new TrackSectorOffset[200, 20];


            int sectorstotal = sectorspertrack * tracksperdisk * numberofheads;

            byte scpformat;
            int[] tracklengthrxbuf = new int[200];

            int averagetimecompensation;
            ProcessingType procmode = ProcessingType.adaptive1;
            if (ProcessingModeComboBox.SelectedItem.ToString() != "")
                procmode = (ProcessingType)Enum.Parse(typeof(ProcessingType), ProcessingModeComboBox.SelectedItem.ToString(), true);

            if (procmode == ProcessingType.adaptive1 || procmode == ProcessingType.adaptive2 || procmode == ProcessingType.adaptive3)
                averagetimecompensation = ((80 - FourvScrollBar.Value) + (120 - SixvScrollBar.Value) + (160 - EightvScrollBar.Value)) / 3;
            else
                averagetimecompensation = 5;

            // FloppyControl app DiskFormat to SCP format
            byte[] fca2ScpDiskFormat = new byte[] {
                0, // 0 not used
                0x04, // 1 AmigaDOS
                0x04, // 2 DiskSpare
                0x41, // 3 PC DS DD 
                0x43, // 4 PC HD
                0x43, // 5 PC 2M
                0x40, // 6 PC SS DD
                0x04, // DiskSpare 984KB
            };

            scpformat = fca2ScpDiskFormat[(int)processing.diskformat];

            //Checking sectorok data
            int track = 0, sector = 0;
            for (track = 0; track < tracksperdisk; track++)
            {
                for (sector = 0; sector < sectorspertrack; sector++)
                {
                    if (processing.sectormap.sectorok[track, sector] != SectorMapStatus.CrcOk)
                        break;
                }
            }

            if (track != tracksperdisk || sector != sectorspertrack)
            {
                tbreceived.Append("Error: not all sectors are good in the sectormap. Can't continue.\r\n");
                return;
            }

            byte[,] sectorchecked = new byte[200, 20];
            int sectorokchecksum = 0;
            int trackhead = tracksperdisk * numberofheads;
            //checking sectordata2 if all sectorok items are represented in sectordata2 
            for (track = 0; track < trackhead; track++)
            {
                for (sector = 0; sector < sectorspertrack; sector++)
                {

                    for (i = 0; i < sectordata2.Count; i++)
                    {
                        if (sectordata2[i].sector == sector && sectordata2[i].track == track)
                        {
                            if (sectordata2[i].mfmMarkerStatus == SectorMapStatus.CrcOk || 
                                sectordata2[i].mfmMarkerStatus == SectorMapStatus.SectorOKButZeroed || 
                                sectordata2[i].mfmMarkerStatus == SectorMapStatus.ErrorCorrected)
                            {
                                sectorchecked[track, sector] = 1;
                                sectorokchecksum++;
                                break;
                            }
                        }
                    }
                }
            }

            if (sectorokchecksum != sectorstotal)
            {
                tbreceived.Append("Not all sectors are represented in sectordata2. Can't continue. \r\n");
                return;
            }

            int offset = 0;
            i = 0;
            int q = 0;

            MFMData sectordataheader;
            MFMData sectordata;
            // Find all track and sector offsets and lengths
            if (processing.procsettings.platform == 0) // PC
            {
                for (track = 0; track < trackhead; track++)
                {
                    for (sector = 0; sector < sectorspertrack; sector++)
                    {
                        for (i = 0; i < sectordata2.Count; i++)
                        {
                            sectordataheader = sectordata2[i];
                            if (sectordataheader.sector == sector && sectordataheader.track == track)
                            {
                                if (sectordataheader.MarkerType == MarkerType.header || sectordataheader.MarkerType == MarkerType.headerAndData)
                                {
                                    if (sectordataheader.DataIndex != 0)
                                        sectordata = sectordata2[sectordataheader.DataIndex];
                                    else continue;
                                    if (sectordata.mfmMarkerStatus == SectorMapStatus.CrcOk || 
                                        sectordata.mfmMarkerStatus == SectorMapStatus.SectorOKButZeroed || 
                                        sectordata.mfmMarkerStatus == SectorMapStatus.ErrorCorrected)
                                    {
                                        TrackSectorOffset tso = new TrackSectorOffset();
                                        tso.offsetstart = sectordataheader.rxbufMarkerPositions;
                                        for (q = i + 1; q < sectordata2.Count; q++)
                                        {
                                            if (sectordata2[q].mfmMarkerStatus != 0 &&
                                                (sectordata2[q].MarkerType == MarkerType.header || sectordata2[q].MarkerType == MarkerType.headerAndData))
                                            {
                                                tso.offsetend = sectordata2[q].rxbufMarkerPositions;
                                                break;
                                            }
                                        }
                                        if (tso.offsetend == 0) tso.offsetend = tso.offsetstart + 10000;
                                        tso.length = tso.offsetend - tso.offsetstart;
                                        if (tso.length > 10000)
                                        {

                                            tso.offsetend = tso.offsetstart + 10000;
                                            tso.length = tso.offsetend - tso.offsetstart;
                                        }
                                        tsoffset[track, sector] = tso;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else // Amiga
            {
                for (track = 0; track < trackhead; track++)
                {
                    for (sector = 0; sector < sectorspertrack; sector++)
                    {
                        for (i = 0; i < sectordata2.Count; i++)
                        {
                            sectordata = sectordata2[i];
                            if (sectordata.sector == sector && sectordata.track == track)
                            {
                                if (sectordata.mfmMarkerStatus == SectorMapStatus.CrcOk || 
                                    sectordata.mfmMarkerStatus == SectorMapStatus.SectorOKButZeroed || 
                                    sectordata.mfmMarkerStatus == SectorMapStatus.ErrorCorrected)
                                {
                                    TrackSectorOffset tso = new TrackSectorOffset();
                                    tso.offsetstart = sectordata.rxbufMarkerPositions;
                                    for (q = i + 1; q < sectordata2.Count; q++)
                                    {
                                        if (sectordata.mfmMarkerStatus != 0 &&
                                            (sectordata.MarkerType == MarkerType.header || sectordata.MarkerType == MarkerType.headerAndData))
                                        {
                                            tso.offsetend = sectordata2[q].rxbufMarkerPositions;
                                            break;
                                        }
                                    }
                                    if (tso.offsetend == 0) tso.offsetend = tso.offsetstart + 10000;
                                    tso.length = tso.offsetend - tso.offsetstart;
                                    if (tso.length > 10000)
                                    {

                                        tso.offsetend = tso.offsetstart + 10000;
                                        tso.length = tso.offsetend - tso.offsetstart;
                                    }
                                    tsoffset[track, sector] = tso;
                                }
                            }
                        }
                    }
                }
            }

            // calculate track time
            UInt32[] trackduration = new UInt32[200];
            int[] indexpulsespertrack = new int[200];
            int val = 0;
            for (track = 0; track < trackhead; track++)
            {
                for (sector = 0; sector < sectorspertrack; sector++)
                {
                    for (i = tsoffset[track, sector].offsetstart; i < tsoffset[track, sector].offsetend; i++)
                    {
                        val = processing.rxbuf[i];
                        trackduration[track] += (UInt32)((val) << 1);
                        if (val < 4) indexpulsespertrack[track]++;
                    }
                }
            }
            for (track = 0; track < trackhead; track++)
            {
                for (sector = 0; sector < sectorspertrack; sector++)
                {
                    tbreceived.Append("T: " + track.ToString("D3") + " S" + sector + "\to1:" +
                        tsoffset[track, sector].offsetstart + "\to2:" +
                        tsoffset[track, sector].offsetend +
                    "\tlength: " + tsoffset[track, sector].length + "\t durcation: " + trackduration[track] + "\r\n");
                }
            }
            UInt32[] offsettable = new UInt32[200];

            // Create offset table, calculated without header offset
            offsettable[0] = 0;
            int perioddataoffset = 0;
            int tracklength = 0;
            int value;
            for (track = 0; track < trackhead; track++)
            {
                offsettable[track] = (UInt32)(perioddataoffset);

                for (sector = 0; sector < sectorspertrack; sector++)
                {
                    value = tsoffset[track, sector].length;
                    perioddataoffset += value * 2;
                    tracklength += value;
                }
                perioddataoffset += 0x10;
                perioddataoffset -= (indexpulsespertrack[track] * 2);
                tracklengthrxbuf[track] = tracklength - indexpulsespertrack[track];
                tracklength = 0;
            }

            // Write period data to disk in bin format
            extension = ".scp";
            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;

            textBoxReceived.AppendText("Path:" + path + "\r\n");

            bool exists = System.IO.Directory.Exists(path);

            while (File.Exists(path))
            {
                binfilecount++;
                path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;
            }

            //Only save if there's any data to save

            //if (processing.diskformat == 3 || processing.diskformat == 4) //PC 720 KB dd or 1440KB hd
            //{
            try
            {
                writer = new BinaryWriter(new FileStream(path, FileMode.Create));
            }
            catch (IOException ex)
            {
                textBoxReceived.AppendText("IO error: " + ex.ToString());
                ioerror = 1;
            }

            if (ioerror == 0) // skip writing on io error
            {
                //Write header
                writer.Write((byte)'S');            // 0
                writer.Write((byte)'C');            // 1
                writer.Write((byte)'P');            // 2
                writer.Write((byte)0x39);           // 3 Version 3.9
                writer.Write((byte)scpformat);      // 4 SCP disk type
                writer.Write((byte)1);            // 5 Number of revolutions
                writer.Write((byte)0);            // 6 Start track
                writer.Write((byte)(trackhead - 1)); // 7 end track
                writer.Write((byte)0);            // 8 Flags (copied from sample scp files)
                writer.Write((byte)0);            // 9 Bit depth of flux period data. Using default 16 bits shorts.
                writer.Write((byte)0);             // A number of heads
                writer.Write((byte)0);            // B reserved byte
                writer.Write((UInt32)0);            // C..F Checksum (not used for now)

                UInt32 headeroffset = (UInt32)(0x10 + (trackhead * 4));

                for (track = 0; track < trackhead; track++)
                {
                    writer.Write((UInt32)((offsettable[track]) + headeroffset));
                }

                qq = 0;
                for (track = 0; track < trackhead; track++)
                {

                    writer.Write((byte)'T');            // 0
                    writer.Write((byte)'R');            // 1
                    writer.Write((byte)'K');            // 2
                    writer.Write((byte)track);          // 0..2 track

                    // We're using one single revolution
                    writer.Write((UInt32)trackduration[track]);             // 0..2 track duration in nanoseconds/25
                    writer.Write((UInt32)tracklengthrxbuf[track]);          // 0..2 track flux periods (length)
                    writer.Write((UInt32)0x10);                    // 0..2


                    byte b1 = 0;
                    byte b2 = 0;
                    //int val;
                    //Save sector status
                    for (sector = 0; sector < sectorspertrack; sector++)
                    {
                        for (i = tsoffset[track, sector].offsetstart; i < tsoffset[track, sector].offsetend; i++)
                        {
                            val = processing.rxbuf[i] + averagetimecompensation;
                            if (val < 4 + averagetimecompensation) continue;
                            b1 = (byte)((val >> 7) & 1);
                            b2 = (byte)(val << 1);
                            if (b2 == 0x1a)
                                qq = 2;
                            writer.Write((byte)b1);
                            writer.Write((byte)b2);
                        }
                    }
                }
                if (writer != null)
                {
                    writer.Flush();
                    writer.Close();
                    writer.Dispose();
                }
            }
            else ioerror = 0;
            //}

        }

        private void AdaptiveScan2()
        {
            int j, k, l;
            float i;
            int FOUR = FourvScrollBar.Value;
            int SIX = SixvScrollBar.Value;
            int EIGHT = EightvScrollBar.Value;
            int OFFSET = OffsetvScrollBar1.Value;
            int step = (int)iESStart.Value;
            for (l = -12; l < 13; l += step)
                for (i = 0.6f; i < 2f; i += 0.2f)
                {
                    if (processing.stop == 1)
                        break;
                    RateOfChangeUpDown.Value = (decimal)i;
                    OffsetvScrollBar1.Value = OFFSET + l;

                    Application.DoEvents();
                    
                    if (processing.procsettings.platform == 0)
                        ProcessPC();
                    else
                        ProcessAmiga();
                }

            OffsetvScrollBar1.Value = OFFSET;
            //processing.sectormap.RefreshSectorMap();
            /*
            if (processing.stop == 0)
                for (j = -9; j < 10; j += 3)
                    for (k = -9; k < 10; k += 3)
                    {
                        GC.Collect();
                        for (l = -9; l < 10; l += 3)
                            for (i = 1f; i < 2f; i += 0.1f)
                            {
                                if (processing.stop == 1)
                                    break;
                                RateOfChangeUpDown.Value = (decimal)i;
                                FourvScrollBar.Value = FOUR + l;
                                SixvScrollBar.Value = SIX + k;
                                EightvScrollBar.Value = EIGHT + j;

                                Application.DoEvents();
                                processing.stop = 0;
                                if (processing.procsettings.platform == 0)
                                    ProcessPC();
                                else
                                    ProcessAmiga();
                            }
                        processing.sectormap.RefreshSectorMap();

                    }
            */
            //processing.sectormap.RefreshSectorMap();
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            DiskFormat diskformat = DiskFormat.unknown;
            if (ChangeDiskTypeComboBox.SelectedItem.ToString() != "")
                diskformat = (DiskFormat)Enum.Parse(typeof(DiskFormat), ChangeDiskTypeComboBox.SelectedItem.ToString(), true);
            tbreceived.Append("Selected: " + diskformat.ToString() + "\r\n");

            processing.diskformat = diskformat;
            
        }

        private void button48_Click(object sender, EventArgs e)
        {
            int track, sector;

            var sectorok = processing.sectormap.sectorok;
            int starttrack = (int)StartTrackUpDown.Value;
            int endtrack = (int)EndTracksUpDown.Value;

            MainTabControl.SelectedTab = ProcessingTab;
            processing.stop = 0;

            for (track = starttrack; track < endtrack + 1; track++)
            {
                for (sector = 0; sector < processing.sectorspertrack; sector++)
                {
                    if (sectorok[track, sector] == 0 || sectorok[track, sector] == SectorMapStatus.HeadOkDataBad)
                    {
                        tbreceived.Append("Track: " + track);
                        StartTrackUpDown.Value = track;

                        EndTracksUpDown.Value = track;
                        CaptureTracks();
                        while (controlfloppy.capturecommand == 1)
                        {
                            Thread.Sleep(20);
                            Application.DoEvents();
                        }
                        Application.DoEvents();
                        AdaptiveScan();
                        Application.DoEvents();
                        if (processing.stop == 1)
                            break;
                        break;
                    }
                }
                if (processing.stop == 1)
                    break;
            }
            StartTrackUpDown.Value = starttrack;
            EndTracksUpDown.Value = endtrack;

            tbreceived.Append("Done!\r\n");
        }

        private void button49_Click(object sender, EventArgs e)
        {
            SaveTrimmedBinFile();
        }

        private void SaveTrimmedBadBinFile()
        {
            int i, j;
            string extension = "";
            int ioerror = 0;
            int qq = 0;
            var sectordata2 = processing.sectordata2;

            // First check if there's sectordata2 data available and that all sectors are ok in sectorok array
            int sectorspertrack = processing.diskGeometry[processing.diskformat].sectorsPerTrack;
            int tracksperdisk = processing.diskGeometry[processing.diskformat].tracksPerDisk;
            int sectorsize = processing.diskGeometry[processing.diskformat].sectorSize;
            int numberofheads = processing.diskGeometry[processing.diskformat].numberOfHeads;

            //TrackSectorOffset[,] tsoffset = new TrackSectorOffset[200, 20];


            int sectorstotal = sectorspertrack * tracksperdisk * numberofheads;

            byte scpformat;
            //int[] tracklengthrxbuf = new int[200];

            int averagetimecompensation;
            ProcessingType procmode = ProcessingType.adaptive1;
            if (ProcessingModeComboBox.SelectedItem.ToString() != "")
                procmode = (ProcessingType)Enum.Parse(typeof(ProcessingType), ProcessingModeComboBox.SelectedItem.ToString(), true);

            if (procmode == ProcessingType.adaptive1 || procmode == ProcessingType.adaptive2 || procmode == ProcessingType.adaptive3)
                averagetimecompensation = ((80 - FourvScrollBar.Value) + (120 - SixvScrollBar.Value) + (160 - EightvScrollBar.Value)) / 3;
            else
                averagetimecompensation = 5;

            // FloppyControl app DiskFormat to SCP format
            byte[] fca2ScpDiskFormat = new byte[] {
                0, // 0 not used
                0x04, // 1 AmigaDOS
                0x04, // 2 DiskSpare
                0x41, // 3 PC DS DD 
                0x43, // 4 PC HD
                0x43, // 5 PC 2M
                0x40, // 6 PC SS DD
                0x04, // DiskSpare 984KB
            };

            scpformat = fca2ScpDiskFormat[(int)processing.diskformat];

            //Checking sectorok data
            int track = 0, sector = 0;
            int trackhead = tracksperdisk * numberofheads;
            i = 0;
            int q = 0;

            // Write period data to disk in bin format
            extension = "_trimmedBad.bin";
            path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;

            textBoxReceived.AppendText("Path:" + path + "\r\n");

            bool exists = System.IO.Directory.Exists(path);

            while (File.Exists(path))
            {
                binfilecount++;
                path = subpath + @"\" + outputfilename.Text + @"\" + outputfilename.Text + "_" + binfilecount.ToString("D3") + extension;
            }

            //Only save if there's any data to save

            //if (processing.diskformat == 3 || processing.diskformat == 4) //PC 720 KB dd or 1440KB hd
            //{
            try
            {
                writer = new BinaryWriter(new FileStream(path, FileMode.Create));
            }
            catch (IOException ex)
            {
                textBoxReceived.AppendText("IO error: " + ex.ToString());
                ioerror = 1;
            }
            int badsectorcnt = 0;
            // Save all bad sectors
            
            MFMData sectordataheader, sectordata;
            if (processing.procsettings.platform == 0) // PC
            {
                for (i = 0; i < sectordata2.Count; i++)
                {
                    sectordataheader = sectordata2[i];
                    if (sectordataheader.MarkerType == MarkerType.header || sectordataheader.MarkerType == MarkerType.headerAndData)
                    {
                        if (sectordataheader.DataIndex != 0)
                            sectordata = sectordata2[sectordataheader.DataIndex];
                        else continue;
                        if (sectordata.mfmMarkerStatus == SectorMapStatus.HeadOkDataBad)
                        {
                            if (processing.sectormap.sectorok[sectordata.track, sectordata.sector] != SectorMapStatus.HeadOkDataBad)
                                continue; // skip if sector is already good
                            TrackSectorOffset tso = new TrackSectorOffset();
                            tso.offsetstart = sectordataheader.rxbufMarkerPositions;
                            for (q = i + 1; q < sectordata2.Count; q++)
                            {
                                if (sectordata2[q].mfmMarkerStatus != 0 &&
                                    (sectordata2[q].MarkerType == MarkerType.header || sectordata2[q].MarkerType == MarkerType.headerAndData))
                                {
                                    tso.offsetend = sectordata2[q].rxbufMarkerPositions;
                                    break;
                                }
                            }
                            if (tso.offsetend == 0) tso.offsetend = tso.offsetstart + 10000;
                            tso.length = tso.offsetend - tso.offsetstart;
                            if (tso.length > 10000)
                            {

                                tso.offsetend = tso.offsetstart + 10000;
                                tso.length = tso.offsetend - tso.offsetstart;
                            }
                            badsectorcnt++;
                            //writer.Write("T" + track.ToString("D3") + "S" + sector);
                            for (q = tso.offsetstart; q < tso.offsetend; q++)
                                writer.Write((byte)(processing.rxbuf[q]));
                            //tsoffset[track, sector] = tso;
                        }
                    }
                }
            }
            else // Amiga
            {
                for (i = 0; i < sectordata2.Count; i++)
                {
                    sectordata = sectordata2[i];
                    if (sectordata.mfmMarkerStatus == SectorMapStatus.HeadOkDataBad)
                    {
                        
                        TrackSectorOffset tso = new TrackSectorOffset();
                        tso.offsetstart = sectordata.rxbufMarkerPositions;
                        for (q = i + 1; q < sectordata2.Count; q++)
                        {
                            if (sectordata.mfmMarkerStatus != 0 &&
                                (sectordata.MarkerType == MarkerType.header || sectordata.MarkerType == MarkerType.headerAndData))
                            {
                                tso.offsetend = sectordata2[q].rxbufMarkerPositions;
                                break;
                            }
                        }
                        if (tso.offsetend == 0) tso.offsetend = tso.offsetstart + 10000;
                        tso.length = tso.offsetend - tso.offsetstart;
                        if (tso.length > 10000)
                        {

                            tso.offsetend = tso.offsetstart + 10000;
                            tso.length = tso.offsetend - tso.offsetstart;
                        }
                        badsectorcnt++;
                        //writer.Write("T" + track.ToString("D3") + "S" + sector);
                        for (q = tso.offsetstart; q < tso.offsetend; q++)
                            writer.Write((byte)(processing.rxbuf[q]));
                        //tsoffset[track, sector] = tso;
                    }
                }
            }

            tbreceived.Append("Bad sectors: "+badsectorcnt+" Trimmed bin file saved succesfully.\r\n");
            if (writer != null)
            {
                writer.Flush();
                writer.Close();
                writer.Dispose();
            }
        }

        private void SaveTrimmedBadbutton_Click(object sender, EventArgs e)
        {
            SaveTrimmedBadBinFile();
        }

        private void AdaptiveScan3()
        {
            int j, l;
            float i, k;
            float adaptrate = (float)RateOfChangeUpDown.Value;
            int FOUR = FourvScrollBar.Value;
            int SIX = SixvScrollBar.Value;
            int EIGHT = EightvScrollBar.Value;
            int OFFSET = OffsetvScrollBar1.Value;
            int step = (int)iESStart.Value;
            for (k = 2; k < 1200; k *= 2)
            {
                RateOfChange2UpDown.Value = (int)k;
                for (l = -12; l < 13; l += step)
                    for (i = 0.6f; i < 2f; i += 0.2f)
                    {
                        if (processing.stop == 1)
                            break;
                        RateOfChangeUpDown.Value = (decimal)i;
                        OffsetvScrollBar1.Value = OFFSET + l;

                        Application.DoEvents();
                        if (processing.procsettings.platform == 0)
                            ProcessPC();
                        else
                            ProcessAmiga();
                    }

            }
            OffsetvScrollBar1.Value = OFFSET;
            //processing.sectormap.RefreshSectorMap();
        }

        private void AdaptiveScan4()
        {
            int j, l;
            float i, k;
            float adaptrate = (float)RateOfChangeUpDown.Value;
            int FOUR = FourvScrollBar.Value;
            int SIX = SixvScrollBar.Value;
            int EIGHT = EightvScrollBar.Value;
            int OFFSET = OffsetvScrollBar1.Value;
            int step = (int)iESStart.Value;
            
            for (l = 0; l < 4; l += step)
                for (i = 0.6f; i < 2f; i += 0.2f)
                {
                    if (processing.stop == 1)
                        break;
                    RateOfChangeUpDown.Value = (decimal)i;
                    OffsetvScrollBar1.Value = OFFSET + l;

                    Application.DoEvents();
                    if (processing.procsettings.platform == 0)
                        ProcessPC();
                    else
                        ProcessAmiga();
                }

            OffsetvScrollBar1.Value = OFFSET;
        }
        private void AdaptiveNarrow()
        {
            int j, l;
            float i, k;
            float adaptrate = (float)RateOfChangeUpDown.Value;
            int FOUR = FourvScrollBar.Value;
            int SIX = SixvScrollBar.Value;
            int EIGHT = EightvScrollBar.Value;
            int OFFSET = OffsetvScrollBar1.Value;
            int step = (int)iESStart.Value;

            for (l = 0; l < 8; l += step)
                //for (i = 0.5f; i < 2f; i += 0.2f)
                {
                    if (processing.stop == 1) break;
                    //RateOfChangeUpDown.Value = (decimal)i;
                    FourvScrollBar.Value = FOUR + l;
                    EightvScrollBar.Value = EIGHT - l;
                    Application.DoEvents();
                    if (processing.procsettings.platform == 0)
                        ProcessPC();
                    else
                        ProcessAmiga();
                }
            FourvScrollBar.Value = FOUR;
            EightvScrollBar.Value = EIGHT;
            OffsetvScrollBar1.Value = OFFSET;
        }
        private void AdaptiveNarrowRate()
        {
            int j, l;
            float i, k;
            float adaptrate = (float)RateOfChangeUpDown.Value;
            int FOUR = FourvScrollBar.Value;
            int SIX = SixvScrollBar.Value;
            int EIGHT = EightvScrollBar.Value;
            int OFFSET = OffsetvScrollBar1.Value;
            int step = (int)iESStart.Value;

            for (l = 0; l < 8; l += step)
            for (i = 0.5f; i < 2f; i += 0.2f)
            {
                if (processing.stop == 1) break;
                RateOfChangeUpDown.Value = (decimal)i;
                FourvScrollBar.Value = FOUR + l;
                EightvScrollBar.Value = EIGHT - l;
                Application.DoEvents();
                processing.stop = 0;
                if (processing.procsettings.platform == 0)
                    ProcessPC();
                else
                    ProcessAmiga();
            }
            FourvScrollBar.Value = FOUR;
            EightvScrollBar.Value = EIGHT;
            OffsetvScrollBar1.Value = OFFSET;
        }

        private void ProcessingModeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            ProcessingType procmode = ProcessingType.adaptive1;
            if (ProcessingModeComboBox.SelectedItem.ToString() != "")
                procmode = (ProcessingType)Enum.Parse(typeof(ProcessingType), ProcessingModeComboBox.SelectedItem.ToString(), true);
            tbreceived.Append("Selected: " + procmode.ToString()+"\r\n");

            
            switch (procmode)
            {
                case ProcessingType.normal:
                    FindPeaks();
                    EightvScrollBar.Value = 0xff;
                    scatterplot.showEntropy = false;
                    break;
                case ProcessingType.aufit:
                    MinvScrollBar.Value = 0x32;
                    FourvScrollBar.Value = 0x0C;
                    OffsetvScrollBar1.Value = 0;
                    scatterplot.showEntropy = false;
                    break;
                case ProcessingType.adaptive1:
                case ProcessingType.adaptive2:
                case ProcessingType.adaptive3:
                    RateOfChangeUpDown.Value = (decimal)1.1;
                    RateOfChange2UpDown.Value = 800;

                    FindPeaks();
                    scatterplot.showEntropy = false;
                    break;
                case ProcessingType.adaptivePredict:
                    RateOfChangeUpDown.Value = (decimal)3;
                    RateOfChange2UpDown.Value = 600;
                    FindPeaks();
                    scatterplot.showEntropy = false;
                    break;
                case ProcessingType.adaptiveEntropy:
                    scatterplot.showEntropy = true;
                    break;
            }
        }

        private void ScanBtn_Click_1(object sender, EventArgs e)
        {
            processing.stop = 0;
            DoScan();
        }

        private void DoScan()
        {
            ScanMode procmode = ScanMode.AdaptiveRate;
            if (ScanComboBox.SelectedItem.ToString() != "")
                procmode = (ScanMode)Enum.Parse(typeof(ScanMode), ScanComboBox.SelectedItem.ToString(), true);
            tbreceived.Append("Selected: " + procmode.ToString() + "\r\n");


            switch (procmode)
            {
                case ScanMode.AdaptiveRate:
                    AdaptiveScan();
                    break;
                case ScanMode.AdaptiveOffsetRate:
                    AdaptiveScan2();
                    break;
                case ScanMode.AdaptiveDeep:
                    AdaptiveScan3();
                    break;
                case ScanMode.AdaptiveShallow:
                    AdaptiveScan4();
                    break;
                case ScanMode.AuScan:
                    AuScan();
                    break;
                case ScanMode.ExtremeScan:
                    ExtremeScan();
                    break;
                case ScanMode.OffsetScan:
                    OffsetScan();
                    break;
                case ScanMode.OffsetScan2:
                    OffsetScan2();
                    break;
                case ScanMode.AdaptiveNarrow:
                    AdaptiveNarrow();
                    break;
                case ScanMode.AdaptiveNarrowRate:
                    AdaptiveNarrowRate();
                    break;
            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            scatterplot.EditScatterplot = EditScatterPlotcheckBox.Checked;
        }

        private void TrackPreset1Button_Click_1(object sender, EventArgs e)
        {
            StartTrackUpDown.Value = 0;
            EndTracksUpDown.Value = 10;
            TrackDurationUpDown.Value = 260;
        }

        private void FullHistBtn_Click(object sender, EventArgs e)
        {
            ScatterHisto.DoHistogram(processing.rxbuf, (int)rxbufStartUpDown.Value, (int)rxbufEndUpDown.Value);
        }

        void updateAllGraphs()
        {
            if (controlfloppy.capturecommand == 1)
            {
                processing.rxbuf = controlfloppy.tempbuffer.Skip(Math.Max(0, controlfloppy.tempbuffer.Count() - 30)).SelectMany(a => a).ToArray();
            }
            else
            {
                processing.rxbuf = controlfloppy.tempbuffer.SelectMany(a => a).ToArray();
            }
            //processing.rxbuf = controlfloppy.tempbuffer.SelectMany(a => a).ToArray();

            Setrxbufcontrol();

            if (processing.indexrxbuf < 100000)
                scatterplot.AnScatViewlength = processing.indexrxbuf;
            else scatterplot.AnScatViewlength = 99999;
            scatterplot.AnScatViewoffset = 0;
            //scatterplot.UpdateScatterPlot();

            //ScatterHisto.DoHistogram(rxbuf, (int)rxbufStartUpDown.Value, (int)rxbufEndUpDown.Value);
            if (processing.indexrxbuf > 0)
                ProcessingTab.Enabled = true;
            if (controlfloppy.capturecommand == 0)
                HistogramhScrollBar1.Value = 0;
            if (processing.indexrxbuf > 0)
            {
                //updateAnScatterPlot();
                scatterplot.AnScatViewlargeoffset = HistogramhScrollBar1.Value;
                scatterplot.UpdateScatterPlot();
                if (controlfloppy.capturecommand == 0)
                {
                    ScatterHisto.DoHistogram();
                    updateSliderLabels();
                    updateHistoAndSliders();
                }
                
            }
        }
    } // end class
} // End namespace
