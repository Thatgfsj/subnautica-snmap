// SNMapSettingsForm - 大地图窗口的【设置】窗口
// 左侧: 小地图大小 / 显示敌对生物(总开关) / 显示扫描室扫描的物品
// 右侧: 全部敌对生物逐个勾选(按物种显示或隐藏)
// 所有改动都写进 SNMapSettings.ini, 游戏内模块每 3 帧(≈100ms)轮询一次 -> 立即生效, 不用重新注入
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SNMap;

internal class SpeciesItem
{
    public string Name;    // TechType 名(写进设置文件的就是它)
    public string Label;   // 显示用: "ReaperLeviathan  死神利维坦"
    public SpeciesItem(string n, string l) { Name = n; Label = l; }
    public override string ToString() { return Label; }
}

public class SNMapSettingsForm : Form
{
    private static readonly Color ThemeBg = Color.FromArgb(232, 241, 252);
    private static readonly Color ThemeText = Color.FromArgb(21, 60, 100);
    private static readonly Color ThemeAccent = Color.FromArgb(25, 118, 210);

    private readonly MapForm owner;
    private NumericUpDown numMini;
    private CheckBox chkCreatures, chkScan;
    private CheckedListBox lstSpecies;
    private Label lblCount;
    private bool loading;   // 装载/批量改勾选时不要回写

    public SNMapSettingsForm(MapForm o)
    {
        owner = o;
        Text = "SNMap 设置";
        ClientSize = new Size(600, 430);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = ThemeBg;
        ForeColor = ThemeText;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        TopMost = o.TopMost;

        // ---------------- 左侧
        GroupBox grpL = new GroupBox();
        grpL.Text = "显示";
        grpL.ForeColor = ThemeText;
        grpL.Location = new Point(12, 10);
        grpL.Size = new Size(196, 405);
        grpL.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
        Controls.Add(grpL);

        Label l1 = new Label();
        l1.Text = "小地图大小 (像素)";
        l1.AutoSize = true;
        l1.Location = new Point(14, 28);
        grpL.Controls.Add(l1);

        numMini = new NumericUpDown();
        numMini.Minimum = 120;
        numMini.Maximum = 800;
        numMini.Increment = 20;
        numMini.Location = new Point(16, 52);
        numMini.Size = new Size(80, 26);
        numMini.ValueChanged += delegate
        {
            if (!loading) owner.MinimapPixels = (int)numMini.Value;
        };
        grpL.Controls.Add(numMini);

        Label l1b = new Label();
        l1b.Text = "(120 - 800, 改完立刻生效)";
        l1b.AutoSize = true;
        l1b.ForeColor = Color.FromArgb(90, 120, 150);
        l1b.Location = new Point(102, 56);
        grpL.Controls.Add(l1b);

        chkCreatures = new CheckBox();
        chkCreatures.Text = "显示敌对生物 (总开关)";
        chkCreatures.AutoSize = true;
        chkCreatures.Location = new Point(16, 100);
        chkCreatures.CheckedChanged += delegate
        {
            if (!loading) owner.ShowCreaturesSetting = chkCreatures.Checked;
        };
        grpL.Controls.Add(chkCreatures);

        chkScan = new CheckBox();
        chkScan.Text = "显示扫描室扫描的物品";
        chkScan.AutoSize = true;
        chkScan.Location = new Point(16, 132);
        chkScan.CheckedChanged += delegate
        {
            if (!loading) owner.ShowScanSignalsSetting = chkScan.Checked;
        };
        grpL.Controls.Add(chkScan);

        Label l2 = new Label();
        l2.Text = "说明:\r\n上面三个开关立即生效, 不用重新注入。\r\n\r\n" +
                  "右侧逐个勾选敌对生物: 勾上=在地图上显示这一种, 去掉=隐藏。\r\n" +
                  "左边的总开关关掉时, 右侧怎么勾都不显示。\r\n\r\n" +
                  "列表里是游戏里存在的敌对物种; 没去过的区域, 物种要等你靠近过才会出现在这里。";
        l2.Location = new Point(14, 170);
        l2.Size = new Size(168, 225);
        l2.ForeColor = Color.FromArgb(60, 100, 140);
        grpL.Controls.Add(l2);

        // ---------------- 右侧
        GroupBox grpR = new GroupBox();
        grpR.Text = "敌对生物 (逐个开关)";
        grpR.ForeColor = ThemeText;
        grpR.Location = new Point(216, 10);
        grpR.Size = new Size(372, 405);
        grpR.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(grpR);

        lstSpecies = new CheckedListBox();
        lstSpecies.CheckOnClick = true;
        lstSpecies.IntegralHeight = false;
        lstSpecies.Location = new Point(12, 26);
        lstSpecies.Size = new Size(348, 300);
        lstSpecies.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        lstSpecies.ItemCheck += delegate(object s, ItemCheckEventArgs e)
        {
            if (loading) return;
            // ItemCheck 触发时勾选状态还没落地, 等状态更新后再回写
            BeginInvoke(new MethodInvoker(delegate { WriteShown(); }));
        };
        grpR.Controls.Add(lstSpecies);

        Button btnAll = MakeBtn("全选", 12, 336, 70);
        btnAll.Click += delegate { SetAll(true); };
        grpR.Controls.Add(btnAll);

        Button btnNone = MakeBtn("全不选", 90, 336, 70);
        btnNone.Click += delegate { SetAll(false); };
        grpR.Controls.Add(btnNone);

        lblCount = new Label();
        lblCount.AutoSize = true;
        lblCount.ForeColor = Color.FromArgb(90, 120, 150);
        lblCount.Location = new Point(172, 342);
        grpR.Controls.Add(lblCount);

        Button btnClose = MakeBtn("关闭", 286, 336, 74);
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClose.Click += delegate { Close(); };
        grpR.Controls.Add(btnClose);

        Load += delegate { LoadValues(); };
    }

    private static Button MakeBtn(string text, int x, int y, int w)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, 30);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 136, 229);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(13, 71, 161);
        b.BackColor = ThemeAccent;
        b.ForeColor = Color.White;
        return b;
    }

    private void LoadValues()
    {
        loading = true;
        numMini.Value = Math.Max(numMini.Minimum, Math.Min(numMini.Maximum, owner.MinimapPixels));
        chkCreatures.Checked = owner.ShowCreaturesSetting;
        chkScan.Checked = owner.ShowScanSignalsSetting;

        List<string> names = owner.SpeciesNames();
        List<string> shown = owner.CurrentShownSpecies();
        lstSpecies.Items.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            SpeciesItem it = new SpeciesItem(names[i], owner.SpeciesLabel(names[i]));
            lstSpecies.Items.Add(it, shown.Contains(names[i]));
        }
        loading = false;
        UpdateCount();
    }

    private void SetAll(bool on)
    {
        loading = true;
        for (int i = 0; i < lstSpecies.Items.Count; i++) lstSpecies.SetItemChecked(i, on);
        loading = false;
        WriteShown();
    }

    private void WriteShown()
    {
        if (loading) return;
        List<string> shown = new List<string>();
        for (int i = 0; i < lstSpecies.Items.Count; i++)
        {
            if (!lstSpecies.GetItemChecked(i)) continue;
            SpeciesItem it = lstSpecies.Items[i] as SpeciesItem;
            if (it != null) shown.Add(it.Name);
        }
        owner.WriteShownSpecies(shown);
        UpdateCount();
    }

    private void UpdateCount()
    {
        int n = 0;
        for (int i = 0; i < lstSpecies.Items.Count; i++) if (lstSpecies.GetItemChecked(i)) n++;
        lblCount.Text = "已显示 " + n + " / " + lstSpecies.Items.Count + " 种";
    }
}
