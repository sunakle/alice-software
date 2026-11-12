using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AliceDesk
{
    internal sealed class Feature
    {
        public string id {get;set;} public string name {get;set;} public string source {get;set;}
        public string implementation {get;set;} public string reference {get;set;} public string host {get;set;}
        public string dependencies {get;set;} public string conflicts {get;set;} public string changes {get;set;}
        public string test {get;set;} public string rollback {get;set;} public bool incompatible {get;set;}
        public override string ToString(){return name;}
    }
    internal sealed class Catalog
    {
        public int schema {get;set;} public Dictionary<string,string> documentation {get;set;} public Feature[] features {get;set;}
        internal static Catalog Load()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("features.json")) {
                if(stream==null)throw new InvalidDataException("Каталог расширений отсутствует в приложении.");
                using(var rdr=new StreamReader(stream,Encoding.UTF8)) return Parse(rdr.ReadToEnd());
            }
        }
        internal static Catalog Parse(string json)
        {
            try {
                var c=new JavaScriptSerializer().Deserialize<Catalog>(json);
                if(c==null || c.schema!=1 || c.documentation==null ||
                   !c.documentation.ContainsKey("defaultSpace") || !c.documentation.ContainsKey("materialLicense") ||
                   c.features==null || c.features.Length<30 ||
                   c.features.Any(f=>f==null || String.IsNullOrWhiteSpace(f.id) || String.IsNullOrWhiteSpace(f.name)) ||
                   c.features.GroupBy(f=>f.id).Any(g=>g.Count()!=1))
                    throw new InvalidDataException("Каталог расширений повреждён или имеет неверный формат.");
                return c;
            } catch(ArgumentException ex) {
                throw new InvalidDataException("Не удалось прочитать каталог расширений.",ex);
            } catch(InvalidOperationException ex) {
                throw new InvalidDataException("Не удалось прочитать каталог расширений.",ex);
            } catch(FormatException ex) {
                throw new InvalidDataException("Не удалось прочитать каталог расширений.",ex);
            } catch(OverflowException ex) {
                throw new InvalidDataException("Не удалось прочитать каталог расширений.",ex);
            } catch(InvalidCastException ex) {
                throw new InvalidDataException("Не удалось прочитать каталог расширений.",ex);
            }
        }
    }
    internal sealed class ExtensionsView : UserControl
    {
        readonly Profile profile;
        readonly ListBox features=new ListBox();
        readonly TextBox info=new TextBox {Multiline=true,ScrollBars=ScrollBars.Vertical,AccessibleName="Описание выбранной возможности"};
        readonly TextBox find=new TextBox();
        readonly Catalog catalog;
        readonly string catalogError;
        readonly FlowLayoutPanel clock=new FlowLayoutPanel();
        internal ExtensionsView(Profile profile)
        {
            this.profile=profile; Dock=DockStyle.Fill;Padding=new Padding(12,9,12,12);
            try { catalog=Catalog.Load(); }
            catch(InvalidDataException ex) { catalogError=ex.Message; }
            var head=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Padding=new Padding(1,2,1,9)};
            head.Controls.Add(new Label{Text="Поиск по каталогу",AutoSize=true,Margin=new Padding(3,7,6,3)});
            find.Width=235;head.Controls.Add(find);
            var split=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));
            features.Dock=DockStyle.Fill;features.HorizontalScrollbar=true;features.AccessibleName="Возможности Станции";
            split.Controls.Add(features,0,0);
            var right=new Panel{Dock=DockStyle.Fill};
            info.Dock=DockStyle.Fill;info.ReadOnly=true;info.BackColor=Color.White;info.Font=new Font("Segoe UI",10);
            var source=new SettingsButton {Text="Открыть источник"};
            source.Click+=(s,e)=>{var f=features.SelectedItem as Feature;if(f!=null)MediaUi.Open(f.source);};
            head.Controls.Add(source);
            right.Controls.Add(info);
            clock.Dock=DockStyle.Top;clock.AutoSize=true;
            var at=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="HH:mm",ShowUpDown=true,Width=70};
            var digits=new Label{AutoSize=true,BackColor=Color.FromArgb(43,54,82),ForeColor=Color.White,Font=new Font("Consolas",20),Text=at.Value.ToString("HH:mm")};
            at.ValueChanged+=(s,e)=>digits.Text=at.Value.ToString("HH:mm");
            clock.Controls.Add(new Label{Text="Макет часов на ПК",AutoSize=true});clock.Controls.Add(at);clock.Controls.Add(digits);
            right.Controls.Add(clock);split.Controls.Add(right,1,0);
            Controls.Add(split);Controls.Add(head);
            features.SelectedIndexChanged+=(s,e)=>RefreshDetails();
            find.TextChanged+=(s,e)=>Filter(); VisibleChanged+=(s,e)=>{if(Visible)RefreshDetails();};
            Filter();
        }
        void Filter()
        {
            if(catalog==null) { features.Items.Clear();clock.Visible=false;info.Text=catalogError;return; }
            features.Items.Clear();features.Items.AddRange(catalog.features.Where(f=>(f.name+" "+f.host+" "+f.reference).IndexOf(find.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)>=0).ToArray());
            if(features.Items.Count!=0)features.SelectedIndex=0;else RefreshDetails();
        }
        void RefreshDetails()
        {
            if(catalog==null){clock.Visible=false;info.Text=catalogError;return;}
            var f=features.SelectedItem as Feature;clock.Visible=f!=null&&f.id=="clock";
            if(f==null){info.Text="Совпадений нет.";return;}
            var check=profile.Get("local.device.checked");var sku=profile.Get("local.device.sku","не определён");var build=profile.Get("local.device.build","не определена");
            var text=new StringBuilder();
            text.AppendLine(f.name).AppendLine(f.implementation).AppendLine();
            if(f.incompatible)text.AppendLine("Опубликованное решение относится к другой модели.");
            else if(check=="")text.AppendLine("Измерений Станции в этом профиле пока нет.");
            else text.AppendLine("Последнее измерение в профиле — "+sku+" · "+build+" · "+check+". Действие требует отдельной проверки.");
            string verification=f.id=="say"?"tts":f.id=="ring"?"led":"";
            string stamp=profile.Get("local.device.stamp");
            if(verification!="" && stamp!="" && profile.Get("local.device.verified."+verification)==stamp)
                text.AppendLine("Владелец подтвердил результат испытания на этой Станции и сборке.");
            else if(verification!="" && stamp!="" && profile.Get("local.device.available."+verification)=="1")
                text.AppendLine("Последнее измерение отвечает предварительным условиям. При запуске раздел «Станция» повторит проверку.");
            string[][] fields={new[]{"Модель и сборка в источнике",f.reference},new[]{"Где выполняется",f.host},new[]{"Условия",f.dependencies},
                new[]{"Что проверить на совместимость",f.conflicts},new[]{"Возможности и изменения",f.changes},new[]{"Как испытать",f.test},new[]{"Как вернуться",f.rollback},
                new[]{"Место",f.implementation=="Встроено"?"Установку внешнего пакета эта карточка не запускает.":catalog.documentation["defaultSpace"]},new[]{"Источник",f.source},
                new[]{"Материалы",catalog.documentation["materialLicense"]}};
            foreach(var field in fields)text.AppendLine().AppendLine(field[0]).AppendLine(field[1]);
            info.Text=text.ToString();info.SelectionStart=0;info.ScrollToCaret();
        }
    }
}
