﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UI.WinForms.Msil
{
    public partial class MainForm : Form
    {
        private readonly Settings Settings = new Settings();

        public MainForm()
        {
            InitializeComponent();
        }
    }
}
