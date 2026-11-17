using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolObservatory.Core;

namespace SchoolObservatory.Desktop;

public partial class MainWindow
{
    private sealed record ImportPreviewDisplay(ImportPlanRow Source)
    {
        public int RowNumber => Source.RowNumber;
        public string RecordKey => Source.RecordKey;
        public string Status => Source.Status switch
        {
            ImportRowStatus.New => "جديد", ImportRowStatus.Update => "تحديث",
            ImportRowStatus.Unchanged => "بلا تغيير", ImportRowStatus.Duplicate => "مكرر",
            ImportRowStatus.Conflict => "متعارض", _ => "غير صالح"
        };
        public string Message => Source.Message;
        public int ChangedFields => Source.Changes.Count;
    }

    private void ShowImportWizard(ImportEntity entity, string? preferredSchoolId = null)
    {
        var root = Stack();
        root.Children.Add(Label("معالج استيراد البيانات التعليمية الآمن", 22, true));
        root.Children.Add(Label(
            "1 اختيار الملف ← 2 التعرف على العناوين وربط الأعمدة ← 3 تدقيق القيم والمعاينة ← " +
            "4 تأكيد المعاملة ← 5 النتيجة. لا تتغير البيانات حتى بعد مراجعة الفروق والتأكيد الصريح.", 14));

        var category = new ComboBox
        {
            Width = 220, ItemsSource = Enum.GetValues<ImportEntity>(),
            SelectedItem = entity, HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(Box(LineOfFields(("نوع السجلات", category))));
        var stage = Label("الخطوة 1 — اختيار ملف", 18, true);
        stage.Foreground = new SolidColorBrush(Color.FromRgb(0, 99, 150));
        root.Children.Add(stage);
        var selectionArea = Stack();
        var columnArea = Stack();
        var reviewArea = Stack();
        var resultArea = Stack();
        root.Children.Add(Box(selectionArea));
        root.Children.Add(Box(columnArea));
        root.Children.Add(Box(reviewArea));
        root.Children.Add(Box(resultArea));

        ImportTable? table = null;
        string filename = "";
        ImportPreview? preview = null;
        var schoolOptions = _store.GetSchools(_actor)
            .Where(x => x.IsActive &&
                (AccessPolicy.CanManageStudents(_actor, x.Id) ||
                 AccessPolicy.CanEditStaff(_actor, x.Id))).ToArray();
        var fallbackSchool = new ComboBox
        {
            Width = 285, ItemsSource = schoolOptions, DisplayMemberPath = "Name",
            SelectedItem = schoolOptions.FirstOrDefault(x => x.Id == preferredSchoolId) ??
                schoolOptions.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        void ClearAfterFile()
        {
            table = null;
            preview = null;
            columnArea.Children.Clear();
            reviewArea.Children.Clear();
            resultArea.Children.Clear();
            stage.Text = "الخطوة 1 — اختيار ملف";
        }

        void RenderMapping()
        {
            if(table is null) return;
            columnArea.Children.Clear();
            reviewArea.Children.Clear();
            resultArea.Children.Clear();
            preview = null;
            var selectedType=(ImportEntity)category.SelectedItem;
            var automatic = new Dictionary<string,int>();
            try
            {
                foreach(var pair in ImportColumnMapping.Auto(selectedType,table.Headers))
                    automatic[pair.Key]=pair.Value;
            }
            catch(ArgumentException)
            {
                // Manual mapping remains available for nonstandard headers.
            }
            stage.Text = "الخطوة 2 — التعرف على العناوين وربط الأعمدة";
            columnArea.Children.Add(Label(
                $"النوع المكتشف: {table.Format.ToUpperInvariant()} | الملف: {Path.GetFileName(filename)} | " +
                $"الأعمدة: {table.Headers.Count} | الصفوف: {table.Rows.Count}", 14, true));
            columnArea.Children.Add(Label(
                "حدد عمود الملف المقابل لكل حقل. «غير موجود» يعني الاحتفاظ بالقيمة الحالية " +
                "عند تحديث السجل، وليس مسحها. المعرف الأساسي مطلوب.", 13));

            var selectors = new Dictionary<string,ComboBox>(StringComparer.OrdinalIgnoreCase);
            var mappingPanel = new WrapPanel();
            foreach(var field in ImportColumnMapping.Fields(selectedType))
            {
                var options = new List<string> { "— غير موجود —" };
                options.AddRange(table.Headers.Select((header,i) => $"{i+1}: {header}"));
                var picker = new ComboBox
                {
                    Width = 250, ItemsSource = options,
                    SelectedIndex = automatic.TryGetValue(field,out var i) ? i+1 : 0,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                selectors.Add(field,picker);
                mappingPanel.Children.Add(FormField(field,picker));
            }
            columnArea.Children.Add(mappingPanel);
            if(selectedType!=ImportEntity.Schools)
            {
                columnArea.Children.Add(Label(
                    "إذا لم يحتو الملف على رمز المدرسة، يمكن تحديد مدرسة افتراضية للسجلات كلها. " +
                    "لن يسمح النظام بإضافة سجلات خارج صلاحيات الحساب.", 13));
                columnArea.Children.Add(LineOfFields(("مدرسة افتراضية",fallbackSchool)));
            }
            columnArea.Children.Add(Btn("الخطوة 3 — تدقيق جميع الصفوف ومعاينة الفروق", async (_, _) =>
            {
                var button = (Button)_;
                button.IsEnabled=false;
                try
                {
                    var mapping = selectors.Where(x=>x.Value.SelectedIndex>0)
                        .ToDictionary(x=>x.Key,x=>x.Value.SelectedIndex-1,StringComparer.OrdinalIgnoreCase);
                    ImportColumnMapping.Validate(selectedType,table.Headers,mapping);
                    var sourceTable=table;
                    var schoolId=selectedType==ImportEntity.Schools ? null :
                        (fallbackSchool.SelectedItem as School)?.Id;
                    var plan = await Task.Run(() => _store.PreviewImport(
                        _actor,selectedType,Path.GetFileName(filename),sourceTable,mapping,schoolId));
                    preview=plan;
                    RenderPreview(plan);
                }
                catch(Exception ex)
                {
                    MessageBox.Show(ex.Message,"تدقيق ملف الاستيراد",
                        MessageBoxButton.OK,MessageBoxImage.Warning);
                }
                finally { button.IsEnabled=true; }
            },tone:"primary"));
        }

        void RenderPreview(ImportPreview plan)
        {
            reviewArea.Children.Clear();
            resultArea.Children.Clear();
            stage.Text = "الخطوة 3 — معاينة وتصنيف السجلات";
            reviewArea.Children.Add(Label(
                $"جديد: {plan.Created} | تحديث: {plan.Updated} | دون تغيير: {plan.Unchanged} | " +
                $"غير صالح أو مكرر أو متعارض: {plan.Rejected}", 17, true));
            var all=plan.Rows.Select(x=>new ImportPreviewDisplay(x)).ToArray();
            var tableGrid=GridFor(all,
                ("صف الملف","RowNumber",false),("معرف السجل","RecordKey",false),
                ("التصنيف","Status",false),("الحقول المتغيرة","ChangedFields",false),
                ("الملاحظات","Message",false));
            tableGrid.MaxHeight=440;
            reviewArea.Children.Add(tableGrid);
            var detail = Stack();
            detail.Children.Add(Label("اختر صفا من الجدول لعرض القيم القديمة والجديدة لكل حقل.",14));
            reviewArea.Children.Add(Box(detail));
            tableGrid.SelectionChanged += (_,_) =>
            {
                detail.Children.Clear();
                if(tableGrid.SelectedItem is not ImportPreviewDisplay chosen) return;
                detail.Children.Add(Label(
                    $"صف {chosen.RowNumber} | {chosen.RecordKey} | {chosen.Status}",16,true));
                if(chosen.Source.Changes.Count==0)
                {
                    detail.Children.Add(Label(chosen.Message,14));
                    return;
                }
                var values=GridFor(chosen.Source.Changes,
                    ("الحقل","Field",false),("القيمة السابقة","Before",false),
                    ("القيمة القادمة","After",false));
                values.MaxHeight=265;
                detail.Children.Add(values);
            };
            if(!plan.CanCommit)
            {
                reviewArea.Children.Add(Label(
                    "لا يمكن اعتماد الدفعة: صحح جميع الصفوف غير الصالحة والمكررة والمتعارضة في الملف، ثم أعد المعاينة.",15,true));
                return;
            }
            reviewArea.Children.Add(Btn("الخطوة 4 — تأكيد الاستيراد بعد مراجعة الفروق",async (_,_) =>
            {
                if(preview!=plan) return;
                var confirm=MessageBox.Show(
                    $"سيتم اعتماد {plan.Created} إضافة و{plan.Updated} تحديث في معاملة واحدة.\n" +
                    $"الملف: {Path.GetFileName(filename)}\n" +
                    $"السنة: {plan.AcademicYear}\n" +
                    "الخلايا الفارغة لا تمحو بيانات موجودة. ستُحفظ الدفعة في سجل التدقيق. هل توافق؟",
                    "تأكيد نهائي قبل الكتابة",MessageBoxButton.YesNo,MessageBoxImage.Warning);
                if(confirm!=MessageBoxResult.Yes)return;
                var button=(Button)_;
                button.IsEnabled=false;
                try
                {
                    var batch=await Task.Run(()=>_store.ApplyImport(_actor,plan));
                    stage.Text="الخطوة 5 — نتيجة الاستيراد";
                    resultArea.Children.Clear();
                    resultArea.Children.Add(Label(
                        $"اكتملت المعاملة بنجاح — رقم الدفعة: {batch.Id}",17,true));
                    resultArea.Children.Add(Label(
                        $"جديد: {batch.Created} | محدث: {batch.Updated} | بلا تغيير: {batch.Unchanged} | " +
                        $"مرفوض: {batch.Rejected}",15));
                    resultArea.Children.Add(Btn("عرض سجل الدفعات والتراجع الآمن",(_,_) =>
                        ShowImportHistory(),tone:"secondary"));
                    reviewArea.Children.Clear();
                    columnArea.Children.Clear();
                }
                catch(Exception ex)
                {
                    MessageBox.Show(ex.Message +
                        "\nلم تعتمد الدفعة. أعد الفحص والمعاينة قبل المحاولة.",
                        "توقف الاستيراد بأمان",MessageBoxButton.OK,MessageBoxImage.Warning);
                }
                finally {button.IsEnabled=true;}
            },tone:"success"));
        }

        category.SelectionChanged += (_,_) =>
        {
            ClearAfterFile();
            selectionArea.Children.Clear();
            AddFileButton();
        };
        void AddFileButton()
        {
            selectionArea.Children.Add(Label(
                "اختر CSV بترميز UTF-8 أو Excel .xlsx أو Word .docx بجداول منظمة. " +
                "الملفات القديمة ذات الماكرو، والصيغ الحسابية، والملفات التالفة مرفوضة.",14));
            selectionArea.Children.Add(Btn("1 — تحديد الملف وقراءة العناوين",async (_,_) =>
            {
                var dialog=new OpenFileDialog
                {
                    Filter="الملفات المدعومة|*.csv;*.xlsx;*.docx|CSV UTF-8|*.csv|Excel|*.xlsx|Word|*.docx",
                    Title="اختر الملف المراد استيراده"
                };
                if(dialog.ShowDialog()!=true)return;
                var button=(Button)_;
                button.IsEnabled=false;
                try
                {
                    var parsed=await Task.Run(()=>ImportTableReader.Read(dialog.FileName));
                    table=parsed;
                    filename=dialog.FileName;
                    RenderMapping();
                }
                catch(Exception ex)
                {
                    MessageBox.Show(ex.Message,"خطأ قراءة ملف الاستيراد",
                        MessageBoxButton.OK,MessageBoxImage.Warning);
                }
                finally {button.IsEnabled=true;}
            },tone:"primary"));
        }
        AddFileButton();
        root.Children.Add(Btn("العودة إلى دليل الملفات / سجل الدفعات",(_,_) =>
            ShowImportHistory(),tone:"secondary"));
        Display("معالج الاستيراد الآمن",root);
    }

    private void ShowImportHistory()
    {
        var root=Stack();
        root.Children.Add(Label("سجل دفعات الاستيراد والمراجعة",21,true));
        root.Children.Add(Label(
            "يعرض مصدر الملف وتاريخه ومستخدِمه وأعداد التغييرات. التراجع ليس حذفا قسريا: " +
            "يرفض تلقائيا عند وجود تعديل لاحق أو سجلات مرتبطة، ولا يمس سجل الطلاب الدراسي.",14));
        var history=_store.GetImportBatches(_actor);
        var grid=GridFor(history,
            ("رقم الدفعة","Id",false),("التاريخ","ImportedUtc",false),
            ("الملف","SourceName",false),("المستخدم","Username",false),
            ("النوع","Entity",false),("جديد","Created",false),
            ("محدث","Updated",false),("بلا تغيير","Unchanged",false),
            ("الحالة","Status",false));
        grid.MaxHeight=520;
        root.Children.Add(Box(grid));
        root.Children.Add(Btn("التراجع عن الدفعة المحددة بأمان",(_,_)=>Safe(()=>
        {
            if(grid.SelectedItem is not ImportBatchSummary selected)
                throw new InvalidOperationException("حدد دفعة من سجل الاستيراد.");
            if(MessageBox.Show(
                $"التراجع عن الدفعة {selected.Id}؟\n" +
                "سيُرفض الإجراء بكامله إذا طرأ تعديل لاحق أو كانت هناك بيانات تاريخية تعتمد عليه.",
                "مراجعة التراجع",MessageBoxButton.YesNo,
                MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            _store.ReverseImport(_actor,selected.Id);
            Info("تم التراجع بأمان وتسجيل الإجراء في سجل العمليات.");
            ShowImportHistory();
        }),tone:"warning"));
        root.Children.Add(Btn("فتح معالج الاستيراد",(_,_)=>
        {
            var kind=AccessPolicy.CanEditSchool(_actor) ? ImportEntity.Schools :
                AccessPolicy.Has(_actor,PermissionFlags.ManageStudents) ?
                    ImportEntity.Students : ImportEntity.Staff;
            ShowImportWizard(kind);
        },tone:"primary"));
        Display("دفعات الاستيراد",root);
    }
}
