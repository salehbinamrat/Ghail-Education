using System.Windows;
using System.Windows.Controls;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private sealed record CouncilTableRow(ParentCouncilMember Item)
    {
        public string CouncilType => Item.CouncilType;
        public string FullName => Item.FullName;
        public string Position => Item.Position;
        public string Phone => Item.Phone;
        public string Status => Item.IsActive ? "نشط" : "مؤرشف";
    }

    private void AddCouncilEditor(StackPanel root, School school)
    {
        var panel = Stack();
        panel.Children.Add(Label("مجلس الآباء ومجلس الأمهات", 19, true));
        panel.Children.Add(Label(
            "أضف الأعضاء بأسمائهم وأرقام هواتفهم وصفاتهم، ثم عدل سجلاتهم أو أرشفها. تظهر بيانات المجلس في ملف المدرسة والمؤشرات العامة مباشرة.", 14));
        var records = _store.GetParentCouncilMembers(_actor, school.Id);
        var grid = GridFor(records.Select(x => new CouncilTableRow(x)),
            ("المجلس", "CouncilType", false), ("الاسم", "FullName", false),
            ("الصفة في المجلس", "Position", false), ("رقم الهاتف", "Phone", false),
            ("الحالة", "Status", false));
        grid.MaxHeight = 310;
        panel.Children.Add(Label(
            $"عدد الأعضاء الحاليين: {records.Count(r => r.IsActive)} — الآباء: {records.Count(r => r.IsActive && r.CouncilType == "مجلس الآباء")}، الأمهات: {records.Count(r => r.IsActive && r.CouncilType == "مجلس الأمهات")}", 15, true));
        panel.Children.Add(grid);

        var editor = Stack();
        editor.Children.Add(Label("إضافة عضو جديد أو تعديل العضو المحدد", 16, true));
        var council = new ComboBox
        {
            ItemsSource = new[] { "مجلس الآباء", "مجلس الأمهات" },
            Width = 170, SelectedIndex = 0
        };
        var name = Field(width: 270);
        var position = Field("عضو", 190);
        var phone = Field(width: 170);
        phone.MaxLength = 40;
        var notes = ProfileNotes("", 1000);
        editor.Children.Add(LineOfFields(("نوع المجلس", council), ("الاسم الكامل", name),
            ("الصفة", position), ("رقم التواصل", phone)));
        editor.Children.Add(Label("ملاحظات العضو", 14));
        editor.Children.Add(notes);
        ParentCouncilMember? selected = null;
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not CouncilTableRow row) return;
            selected = row.Item;
            council.SelectedItem = selected.CouncilType;
            name.Text = selected.FullName;
            position.Text = selected.Position;
            phone.Text = selected.Phone;
            notes.Text = selected.Notes;
        };
        void Reset()
        {
            selected = null;
            grid.SelectedItem = null;
            council.SelectedIndex = 0;
            name.Text = ""; position.Text = "عضو"; phone.Text = ""; notes.Text = "";
        }

        var canEdit = AccessPolicy.CanSeeSchool(_actor, school.Id) &&
            (AccessPolicy.CanEditSchool(_actor) ||
             AccessPolicy.CanEditSchoolProfile(_actor, school.Id));
        var buttons = new WrapPanel();
        buttons.Children.Add(Btn("عضو جديد", (_, _) => Reset(), canEdit, "secondary"));
        buttons.Children.Add(Btn("حفظ بيانات عضو المجلس", (_, _) => Safe(() =>
        {
            _store.SaveParentCouncilMember(_actor, school.Id,
                new ParentCouncilMember(selected?.Id ?? "", school.Id,
                    council.SelectedItem?.ToString() ?? "",
                    name.Text, position.Text, phone.Text, notes.Text,
                    selected?.IsActive ?? true));
            Info("تم حفظ العضو وإضافته تلقائيا لمجلس المدرسة والمؤشرات.");
            ShowSchoolProfile(school.Id);
        }), canEdit, "success"));
        buttons.Children.Add(Btn("حذف من المجلس (أرشفة)", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد العضو من الكشف أولا.");
            if (MessageBox.Show("سيتم استبعاد العضو من الأعداد مع الاحتفاظ بسجل التعديل. هل تؤكد الأرشفة؟",
                "تأكيد الأرشفة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _store.SetParentCouncilMemberActive(_actor, school.Id, selected.Id, false);
            ShowSchoolProfile(school.Id);
        }), canEdit, "danger"));
        buttons.Children.Add(Btn("استعادة العضو المؤرشف", (_, _) => Safe(() =>
        {
            if (selected is null) throw new InvalidOperationException("حدد العضو المراد استعادته.");
            _store.SetParentCouncilMemberActive(_actor, school.Id, selected.Id, true);
            ShowSchoolProfile(school.Id);
        }), canEdit, "secondary"));
        editor.Children.Add(buttons);
        editor.IsEnabled = canEdit;
        panel.Children.Add(Box(editor));
        root.Children.Add(Box(panel));
    }

    /// <summary>
    /// A school staff editor is not another table. It writes the same staff record
    /// displayed by the directorate personnel directory, preventing divergence.
    /// </summary>
    private void AddQuickStaffEditor(StackPanel root, School school)
    {
        if (!AccessPolicy.CanEditStaff(_actor, school.Id)) return;
        var section = Stack();
        section.Children.Add(Label("إضافة معلم أو إداري إلى المدرسة", 17, true));
        section.Children.Add(Label(
            "الحفظ هنا يضيف السجل تلقائيا إلى كادر المديرية والملف الوظيفي لهذه المدرسة دون إعادة إدخال.", 14));
        var code = Field(width: 155);
        code.ToolTip = "الرقم الوظيفي الرسمي، إن كان موجودا. لا تخلط بينه وبين رقم التسلسل في الكشف.";
        var name = Field(width: 270);
        var category = new ComboBox
        {
            ItemsSource = new[] { "معلم", "إداري", "أخصائي اجتماعي" },
            Width = 190, SelectedIndex = 0
        };
        var title = Field(width: 220);
        var specialization = Field(width: 200);
        var qualification = Field(width: 200);
        var gender = new ComboBox
        {
            ItemsSource = new[] { "ذكور", "إناث", "غير محدد" }, Width = 150,
            SelectedIndex = 2
        };
        section.Children.Add(LineOfFields(("الرقم الوظيفي", code), ("الاسم", name),
            ("الفئة", category), ("المسمى", title),
            ("التخصص", specialization), ("المؤهل", qualification),
            ("الجنس", gender)));
        section.Children.Add(Btn("إضافة الكادر وربطه بالمدرسة", (_, _) => Safe(() =>
        {
            _store.SaveSchoolStaff(_actor, new SchoolStaff("", school.Id, code.Text, name.Text,
                category.SelectedItem?.ToString() ?? "", title.Text,
                specialization.Text, qualification.Text,
                gender.SelectedItem?.ToString() ?? "غير محدد", true));
            Info("تم تسجيل المعلم أو الإداري، وهو ظاهر تلقائيا في كادر المديرية.");
            ShowSchoolProfile(school.Id);
        }), tone: "success"));
        root.Children.Add(Box(section));
    }
}
