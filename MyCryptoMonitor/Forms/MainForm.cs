﻿using Newtonsoft.Json;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System.Linq;
using System;
using System.IO;
using System.Reflection;
using System.Drawing;
using MyCryptoMonitor.DataSources;
using MyCryptoMonitor.Api;
using MyCryptoMonitor.Statics;
using MyCryptoMonitor.Gui;
using MyCryptoMonitor.Configs;
using MyCryptoMonitor.Objects;

namespace MyCryptoMonitor.Forms
{
    public partial class MainForm : Form
    {
        #region Constant Variables
        private const string API_COIN_MARKET_CAP = "https://api.coinmarketcap.com/v1/ticker/?limit=9999&convert={0}";
        private const string API_CRYPTO_COMPARE = "https://min-api.cryptocompare.com/data/pricemultifull?tsyms={0}&fsyms={1}";
        #endregion

        #region Private Variables
        private List<CoinConfig> _coinConfigs;
        private List<CoinLine> _coinGuiLines;
        private List<Coin> _cryptoCompareCoins;
        private List<Coin> _coinMarketCapCoins;
        private DateTime _resetTime;
        private DateTime _refreshTime;
        private bool _loadGuiLines;
        private string _selectedCoins;
        private string _selectedPortfolio;
        #endregion

        #region Load
        public MainForm()
        {
            InitializeComponent();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _coinGuiLines = new List<CoinLine>();
            _coinConfigs = new List<CoinConfig>();
            _cryptoCompareCoins = new List<Coin>();
            _coinMarketCapCoins = new List<Coin>();
            _resetTime = DateTime.Now;
            _refreshTime = DateTime.Now;
            _loadGuiLines = true;

            //Load user config
            UserConfigService.Load();

            //Unlock if encryption is enabled
            if (UserConfigService.Encrypted)
                EncryptionService.Unlock();

            AlertService.Load();

            //Attempt to load portfolio on startup
            _coinConfigs = PortfolioService.LoadStartup();
            _selectedPortfolio = PortfolioService.CurrentPortfolio;

            //Set currency
            cbCurrency.Text = string.IsNullOrEmpty(UserConfigService.Currency) ? "USD" : UserConfigService.Currency;

            //Add list of coins in config for crypto compare api
            foreach (var name in _coinConfigs)
                _selectedCoins += $",{name.Coin}";

            //Update status
            UpdateStatus("Loading");

            foreach(var portfolio in PortfolioService.GetPortfolios())
            {
                savePortfolioMenu.DropDownItems.Insert(0, new ToolStripMenuItem(portfolio.Name, null, SavePortfolio_Click) { Name = portfolio.Name, Checked = portfolio.Startup });
                loadPortfolioMenu.DropDownItems.Insert(0, new ToolStripMenuItem(portfolio.Name, null, LoadPortfolio_Click) { Name = portfolio.Name, Checked = portfolio.Startup });
            }

            //Start main thread
            Thread mainThread = new Thread(new ThreadStart(DownloadData));
            mainThread.IsBackground = true;
            mainThread.Start();

            //Start time thread
            Thread timerThread = new Thread(new ThreadStart(TimerThread));
            timerThread.IsBackground = true;
            timerThread.Start();

            //Start check update thread
            Thread checkUpdateThread = new Thread(new ThreadStart(CheckUpdate));
            checkUpdateThread.IsBackground = true;
            checkUpdateThread.Start();
        }
        #endregion

        #region Threads
        private void DownloadData()
        {
            while (true)
            {
                UpdateStatus("Refreshing");

                try
                {
                    string currency = (string)cbCurrency.Invoke(new Func<string>(() => cbCurrency.Text));

                    using (var webClient = new WebClient())
                        UpdateCoins(webClient.DownloadString(string.Format(API_CRYPTO_COMPARE, currency, _selectedCoins)), webClient.DownloadString(string.Format(API_COIN_MARKET_CAP, currency)));
                }
                catch (WebException)
                {
                    UpdateStatus("No internet connection");
                }

                UpdateStatus("Sleeping");
                Thread.Sleep(5000);
            }
        }

        private void TimerThread()
        {
            while (true)
            {
                TimeSpan spanReset = DateTime.Now.Subtract(_resetTime);
                TimeSpan spanRefresh = DateTime.Now.Subtract(_refreshTime);
                string resetTime = $"Time since reset: {spanReset.Hours}:{spanReset.Minutes:00}:{spanReset.Seconds:00}";
                string refreshTime = $"Time since refresh: {spanRefresh.Minutes}:{spanRefresh.Seconds:00}";
                
                UpdateTimers(resetTime, refreshTime);

                Thread.Sleep(500);
            }
        }

        private void CheckUpdate()
        {
            bool checkingUpdate = true;
            int attempts = 0;

            while (checkingUpdate && attempts < 5)
            {
                try
                {
                    using (var webClient = new WebClient())
                    {
                        //Github api requires a user agent
                        webClient.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2;)");

                        //Download lastest release data from GitHub
                        string response = webClient.DownloadString("https://api.github.com/repos/Crowley2012/MyCryptoMonitor/releases/latest");
                        ApiGithub release = JsonConvert.DeserializeObject<ApiGithub>(response);

                        //Parse versions
                        Version currentVersion = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());
                        Version latestVersion = new Version(release.tag_name);

                        //Check if latest is newer than current
                        if (currentVersion.CompareTo(latestVersion) < 0)
                        {
                            //Ask if user wants to open github page
                            if (MessageBox.Show($"Download new version?\n\nCurrent Version: {currentVersion}\nLatest Version {latestVersion}", "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == DialogResult.Yes)
                                System.Diagnostics.Process.Start("https://github.com/Crowley2012/MyCryptoMonitor/releases/latest");
                        }

                        checkingUpdate = false;
                    }
                }
                catch (WebException)
                {
                    attempts++;
                }
            }
            
            UpdateStatus("Failed checking for update");
        }
        #endregion

        #region Delegates
        private void UpdateStatus(string status)
        {
            Invoke((MethodInvoker)delegate
            {
                lblStatus.Text = $"Status: {status}";
            });
        }

        private void UpdateTimers(string resetTime, string refreshTime)
        {
            Invoke((MethodInvoker)delegate
            {
                lblResetTime.Text = resetTime;
                lblRefreshTime.Text = refreshTime;
            });
        }

        private void UpdateCoins(string cryptoCompareResponse, string coinMarketCapResponse)
        {
            //Overall values
            decimal totalPaid = 0;
            decimal overallTotal = 0;
            decimal totalNegativeProfits = 0;
            decimal totalPostivieProfits = 0;

            //Index of coin gui line
            int index = 0;

            //Deserialize settings
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore
            };

            //Deserialize response
            _cryptoCompareCoins = MappingService.MapCombination(cryptoCompareResponse, coinMarketCapResponse);

            if(_cryptoCompareCoins.Any(c => c.ShortName.Equals("NANO")))
                _cryptoCompareCoins.Where(c => c.ShortName.Equals("NANO")).FirstOrDefault().ShortName = "XRB";

            //Create list of coin names
            _coinMarketCapCoins = MappingService.CoinMarketCap(coinMarketCapResponse).OrderBy(c => c.ShortName).ToList();
            _coinMarketCapCoins.Where(c => c.ShortName.Equals("NANO")).FirstOrDefault().ShortName = "XRB";

            //Loop through alerts
            if (AlertService.Alerts.Count > 0)
            {
                List<AlertDataSource> removeAlerts = new List<AlertDataSource>();

                foreach (AlertDataSource coin in AlertService.Alerts)
                {
                    var coinData = _cryptoCompareCoins.Where(c => c.ShortName.Equals(coin.Coin) && UserConfigService.Currency.Equals(coin.Currency)).FirstOrDefault();

                    if (coinData == null)
                        continue;

                    if ((coin.Operator.Equals("Greater Than") && coinData.Price > coin.Price) || (coin.Operator.Equals("Less Than") && coinData.Price < coin.Price))
                    {
                        AlertService.SendAlert(coin);
                        removeAlerts.Add(coin);
                    }
                }

                if (removeAlerts.Count > 0)
                    AlertService.Remove(removeAlerts);
            }

            Invoke((MethodInvoker)delegate
            {

                //Loop through all coins from config
                foreach (CoinConfig coin in _coinConfigs)
                {
                    Coin downloadedCoin;

                    //Parse coins, if coin doesnt exist set to 0
                    downloadedCoin = _cryptoCompareCoins.Any(c => c.ShortName == coin.Coin)
                        ? _cryptoCompareCoins.Single(c => c.ShortName == coin.Coin)
                        : new Coin { ShortName = coin.Coin, CoinIndex = coin.CoinIndex, Change1HourPercent = 0, Change24HourPercent = 0, Price = 0 };

                    //Check if gui lines need to be loaded
                    if (_loadGuiLines)
                        AddCoin(coin, downloadedCoin, index);

                    //Store the intial coin price at startup
                    if (coin.SetStartupPrice)
                    {
                        coin.StartupPrice = downloadedCoin.Price;
                        coin.SetStartupPrice = false;
                    }

                    //Incremenet coin line index
                    index++;

                    //Check if coinguiline exists for coinConfig
                    if (!_coinGuiLines.Any(cg => cg.CoinName.Equals(downloadedCoin.ShortName)))
                    {
                        RemoveGuiLines();
                        return;
                    }

                    //Get the gui line for coin
                    CoinLine line = (from c in _coinGuiLines where c.CoinName.Equals(downloadedCoin.ShortName) && c.CoinIndex == coin.CoinIndex select c).First();

                    if (string.IsNullOrEmpty(line.BoughtTextBox.Text))
                        line.BoughtTextBox.Text = "0";

                    if (string.IsNullOrEmpty(line.PaidTextBox.Text))
                        line.PaidTextBox.Text = "0";

                    //Calculate
                    decimal bought = Convert.ToDecimal(line.BoughtTextBox.Text);
                    decimal paid = Convert.ToDecimal(line.PaidTextBox.Text);
                    decimal boughtPrice = bought == 0 ? 0 : paid / bought;
                    decimal total = bought * downloadedCoin.Price;
                    decimal profit = total - paid;
                    decimal changeDollar = downloadedCoin.Price - coin.StartupPrice;
                    decimal changePercent = coin.StartupPrice == 0 ? 0 : ((downloadedCoin.Price - coin.StartupPrice) / coin.StartupPrice) * 100;

                    //Update total profits
                    if (profit >= 0)
                        totalPostivieProfits += profit;
                    else
                        totalNegativeProfits += profit;

                    //Add to totals
                    totalPaid += paid;
                    overallTotal += paid + profit;

                    //Update gui
                    line.CoinLabel.Show();
                    line.CoinIndexLabel.Text = _coinConfigs.Count(c => c.Coin.Equals(coin.Coin)) > 1 ? $"[{coin.CoinIndex + 1}]" : string.Empty;
                    line.CoinLabel.Text = downloadedCoin.ShortName;
                    line.PriceLabel.Text = $"${downloadedCoin.Price}";
                    line.BoughtPriceLabel.Text = $"${boughtPrice:0.000000}";
                    line.TotalLabel.Text = $"${total:0.00}";
                    line.ProfitLabel.Text = $"${profit:0.00}";
                    line.RatioLabel.Text = paid != 0 ? $"{profit/paid:0.00}" : "0.00";
                    line.ChangeDollarLabel.Text = $"${changeDollar:0.000000}";
                    line.ChangePercentLabel.Text = $"{changePercent:0.00}%";
                    line.Change1HrPercentLabel.Text = $"{downloadedCoin.Change1HourPercent:0.00}%";
                    line.Change24HrPercentLabel.Text = $"{downloadedCoin.Change24HourPercent:0.00}%";
                    line.Change7DayPercentLabel.Text = $"{downloadedCoin.Change7DayPercent:0.00}%";
                }

                //Update gui
                lblOverallTotal.Text = $"${overallTotal:0.00}";
                lblTotalProfit.ForeColor = overallTotal - totalPaid >= 0 ? Color.Green : Color.Red;
                lblTotalProfit.Text = $"${overallTotal - totalPaid:0.00}";
                lblTotalNegativeProfit.Text = $"${totalNegativeProfits:0.00}";
                lblTotalPositiveProfit.Text = $"${totalPostivieProfits:0.00}";
                lblStatus.Text = "Status: Sleeping";
                _refreshTime = DateTime.Now;

                _loadGuiLines = false;
                alertsToolStripMenuItem.Enabled = true;
            });
        }

        private void RemoveGuiLines()
        {
            Invoke((MethodInvoker)delegate
            {
                //Set status
                lblStatus.Text = "Status: Loading";

                //Reset totals
                lblOverallTotal.Text = "$0.00";
                lblTotalProfit.Text = "$0.00";

                //Remove the line elements from gui
                foreach (var coin in _coinGuiLines)
                {
                    Height -= 25;
                    coin.Dispose();
                }

                _coinGuiLines = new List<CoinLine>();
                _loadGuiLines = true;
            });
        }
        #endregion

        #region Methods
        private void AddCoin(CoinConfig coin, Coin downloadedCoin, int index)
        {
            //Store the intial coin price at startup
            if(coin.SetStartupPrice)
                coin.StartupPrice = downloadedCoin.Price;

            //Create the gui line
            CoinLine newLine = new CoinLine(downloadedCoin.ShortName, coin.CoinIndex, index);

            //Set the bought and paid amounts
            newLine.BoughtTextBox.Text = coin.Bought.ToString();
            newLine.PaidTextBox.Text = coin.Paid.ToString();
            
            Height += 25;
            Controls.Add(newLine.CoinIndexLabel);
            Controls.Add(newLine.CoinLabel);
            Controls.Add(newLine.PriceLabel);
            Controls.Add(newLine.BoughtTextBox);
            Controls.Add(newLine.BoughtPriceLabel);
            Controls.Add(newLine.TotalLabel);
            Controls.Add(newLine.PaidTextBox);
            Controls.Add(newLine.ProfitLabel);
            Controls.Add(newLine.RatioLabel);
            Controls.Add(newLine.ChangeDollarLabel);
            Controls.Add(newLine.ChangePercentLabel);
            Controls.Add(newLine.Change1HrPercentLabel);
            Controls.Add(newLine.Change24HrPercentLabel);
            Controls.Add(newLine.Change7DayPercentLabel);

            //Add line to list
            _coinGuiLines.Add(newLine);
        }

        private void SavePortfolio(string portfolio)
        {
            if (_coinGuiLines.Count <= 0)
                return;

            List<CoinConfig> coinConfigs = new List<CoinConfig>();

            //Create new list of gui lines
            foreach (var coinLine in _coinGuiLines)
            {
                coinConfigs.Add(new CoinConfig
                {
                    Coin = coinLine.CoinLabel.Text,
                    CoinIndex = coinConfigs.Count(c => c.Coin.Equals(coinLine.CoinName)),
                    Bought = Convert.ToDecimal(coinLine.BoughtTextBox.Text),
                    Paid = Convert.ToDecimal(coinLine.PaidTextBox.Text)
                });
            }

            //Save portfolio
            PortfolioService.Save(portfolio, coinConfigs);
        }

        private void LoadPortfolio(string portfolio)
        {
            _coinConfigs = PortfolioService.Load(portfolio);

            _selectedCoins = string.Empty;
            foreach (var coin in _coinConfigs)
                _selectedCoins += coin.Coin.ToUpper() + ",";

            RemoveGuiLines();
        }

        private void ResetCoinIndex()
        {
            //Reset the coin index
            foreach (CoinConfig config in _coinConfigs)
            {
                int index = 0;

                foreach(CoinConfig coin in _coinConfigs.Where(c => c.Coin == config.Coin).ToList())
                {
                    coin.CoinIndex = index;
                    index++;
                }
            }
        }
        #endregion

        #region Events
        private void Reset_Click(object sender, EventArgs e)
        {
            _resetTime = DateTime.Now;
            LoadPortfolio(PortfolioService.CurrentPortfolio);
        }

        private void Exit_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void AddCoin_Click(object sender, EventArgs e)
        {
            //Check if coin list has been downloaded
            while(_coinMarketCapCoins.Count <= 0)
            {
                if (MessageBox.Show("Please wait while coin list is being downloaded.", "Loading", MessageBoxButtons.RetryCancel) == DialogResult.Cancel)
                    return;
            }

            //Get coin to add
            ManageCoins form = new ManageCoins("Add", _coinMarketCapCoins);
            if (form.ShowDialog() != DialogResult.OK)
                return;

            //Check if coin exists
            if (!_coinMarketCapCoins.Any(c => c.ShortName.Equals(form.InputText.ToUpper())))
            {
                MessageBox.Show("Coin does not exist.", "Error");
                return;
            }

            _selectedCoins += $",{form.InputText.ToUpper()}";

            //Update coin config bought and paid values
            foreach (var coinGuiLine in _coinGuiLines)
            {
                var coinConfig = _coinConfigs.Single(c => c.Coin == coinGuiLine.CoinName && c.CoinIndex == coinGuiLine.CoinIndex);
                coinConfig.Bought = Convert.ToDecimal(coinGuiLine.BoughtTextBox.Text);
                coinConfig.Paid = Convert.ToDecimal(coinGuiLine.PaidTextBox.Text);
                coinConfig.SetStartupPrice = false;
            }

            //Add coin config
            _coinConfigs.Add(new CoinConfig { Coin = form.InputText.ToUpper(), CoinIndex = _coinConfigs.Count(c => c.Coin.Equals(form.InputText.ToUpper())), Bought = 0, Paid = 0, StartupPrice = 0, SetStartupPrice = true });
            RemoveGuiLines();

            _selectedCoins = string.Empty;
            foreach (var coin in _coinConfigs)
                _selectedCoins += coin.Coin.ToUpper() + ",";

            UncheckPortfolios(string.Empty);
        }

        private void RemoveCoin_Click(object sender, EventArgs e)
        {
            //Get coin to remove
            ManageCoins form = new ManageCoins("Remove", _coinConfigs);
            if (form.ShowDialog() != DialogResult.OK)
                return;

            //Check if coin exists
            if (!_coinConfigs.Any(a => a.Coin.Equals(form.InputText.ToUpper()) && a.CoinIndex == form.CoinIndex))
            {
                MessageBox.Show("Coin does not exist.", "Error");
                return;
            }

            //Update coin config bought and paid values
            foreach (var coinGuiLine in _coinGuiLines)
            {
                var coinConfig = _coinConfigs.Single(c => c.Coin == coinGuiLine.CoinName && c.CoinIndex == coinGuiLine.CoinIndex);
                coinConfig.Bought = Convert.ToDecimal(coinGuiLine.BoughtTextBox.Text);
                coinConfig.Paid = Convert.ToDecimal(coinGuiLine.PaidTextBox.Text);
                coinConfig.SetStartupPrice = false;
            }

            //Remove coin config
            _coinConfigs.RemoveAll(a => a.Coin.Equals(form.InputText.ToUpper()) && a.CoinIndex == form.CoinIndex);

            //Reset coin indexes
            ResetCoinIndex();
            RemoveGuiLines();

            UncheckPortfolios(string.Empty);
        }

        private void RemoveAllCoins_Click(object sender, EventArgs e)
        {
            _coinConfigs = new List<CoinConfig>();
            RemoveGuiLines();
            UncheckPortfolios(string.Empty);
        }

        private void UncheckPortfolios(string portfolio)
        {
            _selectedPortfolio = portfolio;

            foreach (ToolStripMenuItem item in savePortfolioMenu.DropDownItems.OfType<ToolStripMenuItem>())
            {
                item.Checked = false;

                if (item.Text.Equals(portfolio))
                    item.Checked = true;
            }

            foreach (ToolStripMenuItem item in loadPortfolioMenu.DropDownItems.OfType<ToolStripMenuItem>())
            {
                item.Checked = false;

                if (item.Text.Equals(portfolio))
                    item.Checked = true;
            }
        }

        private void SavePortfolio_Click(object sender, EventArgs e)
        {
            var portfolio = ((ToolStripMenuItem)sender).Text;

            UncheckPortfolios(portfolio);
            SavePortfolio(portfolio);
        }

        private void LoadPortfolio_Click(object sender, EventArgs e)
        {
            var portfolio = ((ToolStripMenuItem)sender).Text;

            UncheckPortfolios(portfolio);
            LoadPortfolio(portfolio);
        }

        private void donateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PopupDonate form = new PopupDonate();
            form.Show();
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PopupAbout form = new PopupAbout();
            form.Show();
        }

        private void alertsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ManageAlerts form = new ManageAlerts(_cryptoCompareCoins);
            form.Show();
        }

        private void encryptToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ManageEncryption form = new ManageEncryption();
            form.Show();
        }

        private void cbCurrency_SelectedIndexChanged(object sender, EventArgs e)
        {
            alertsToolStripMenuItem.Enabled = false;

            foreach (var config in _coinConfigs)
                config.SetStartupPrice = true;

            UserConfigService.Currency = cbCurrency.Text;
        }

        private void manage_Click(object sender, EventArgs e)
        {
            UncheckPortfolios(_selectedPortfolio);

            PortfolioService.GetPortfolios();

            foreach (var portfolio in PortfolioService.GetPortfolios())
            {
                if (loadPortfolioMenu.DropDownItems.ContainsKey(portfolio.Name))
                {
                    savePortfolioMenu.DropDownItems.RemoveByKey(portfolio.Name);
                    loadPortfolioMenu.DropDownItems.RemoveByKey(portfolio.Name);
                }
            }

            ManagePortfolios form = new ManagePortfolios();
            form.ShowDialog();

            PortfolioService.GetPortfolios();

            foreach (var portfolio in PortfolioService.GetPortfolios())
            {
                if (!loadPortfolioMenu.DropDownItems.ContainsKey(portfolio.Name))
                {
                    savePortfolioMenu.DropDownItems.Insert(0, new ToolStripMenuItem(portfolio.Name, null, SavePortfolio_Click) { Name = portfolio.Name, Checked = portfolio.Name.Equals(_selectedPortfolio) });
                    loadPortfolioMenu.DropDownItems.Insert(0, new ToolStripMenuItem(portfolio.Name, null, LoadPortfolio_Click) { Name = portfolio.Name, Checked = portfolio.Name.Equals(_selectedPortfolio) });
                }
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Environment.Exit(Environment.ExitCode);
        }
        #endregion
    }
}
