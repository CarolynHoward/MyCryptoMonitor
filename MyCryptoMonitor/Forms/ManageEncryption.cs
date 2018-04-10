﻿using MyCryptoMonitor.Statics;
using System;
using System.Windows.Forms;

namespace MyCryptoMonitor.Forms
{
    public partial class ManageEncryption : Form
    {
        public ManageEncryption()
        {
            InitializeComponent();
        }

        private void Encrypt_Load(object sender, EventArgs e)
        {
            cbEnableEncryption.Checked = UserConfigService.Encrypted;
            btnEncrypt.Text = UserConfigService.Encrypted ? "Decrypt" : "Encrypt";
            lblInstructions.Text = UserConfigService.Encrypted ? "Type in your password to disable encryption." : "Type in a password to enable encryption.";
        }

        private void btnEncrypt_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtPassword.Text))
                return;

            if (UserConfigService.Encrypted && EncryptionService.CheckPassword(txtPassword.Text))
            {
                EncryptionService.DecryptFiles();

                cbEnableEncryption.Checked = false;
                btnEncrypt.Text = "Encrypt";
                lblInstructions.Text = "Type in a password to enable encryption.";
            }
            else if (!UserConfigService.Encrypted)
            {
                EncryptionService.EncryptFiles(txtPassword.Text);

                cbEnableEncryption.Checked = true;
                btnEncrypt.Text = "Decrypt";
                lblInstructions.Text = "Type in your password to disable encryption.";
            }
        }
    }
}
