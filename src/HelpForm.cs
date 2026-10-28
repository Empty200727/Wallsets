namespace Wallsets;

// Short help opened by the "?" button in the settings window.
internal sealed class HelpForm : Form
{
    static readonly (string Title, string Text)[] Topics =
    [
        ("С чего начать", "Слева отметьте галочкой набор — его обои сразу появятся на рабочем столе. Для пробы есть «Тестовый набор» с белым и чёрным фоном."),
        ("Свои обои", "Нажмите кнопку с плюсом над списком наборов, введите название — откроется папка нового набора. Положите в неё видео (MP4, MKV, WebM и другие) или картинки (JPG, PNG и другие). Wallsets сам заметит новые файлы. Можно и просто создать папку внутри папки «Наборы» рядом с программой."),
        ("Очередь", "Обои идут слева направо и сверху вниз. Перетащите превью мышью, чтобы поменять порядок. «Порядок» → «Перемешать» — случайный порядок. Двойной щелчок по превью сразу ставит эти обои на рабочий стол. В режиме «Общая очередь» можно отметить несколько наборов, в режиме «Выбранный набор» — только один."),
        ("Сохранённые очереди", "«Сохранить очередь» запоминает текущий порядок под вашим названием. Он появляется в списке «Порядок», когда отмечены те же наборы. Очередь не ломается, если переименовать набор или файлы; новые обои добавляются в её конец. Перетаскивание превью меняет показанную сохранённую очередь; кнопка с корзиной рядом удаляет её."),
        ("Щелчок правой кнопкой по превью", "«Сколько раз в очереди» — чтобы обои шли чаще: новые копии встают сразу справа от них, а при уменьшении лишние убираются с конца очереди. Число хранится в наборе. «Проигрывать со звуком» включает звук только этим обоям, даже когда звук остальных выключен."),
        ("Панель внизу", "Кнопки слева: предыдущие обои, пауза, следующие. «Смена через» показывает, когда сменятся обои; интервал задаётся в поле «Менять каждые … сек»; с галочкой «по длине видео» видео сменяется, когда доиграет до конца (картинки — через интервал). После перезагрузки Wallsets продолжает с тех же обоев. Кнопка с динамиком включает звук обоев, ползунок рядом — их громкость (громкость Windows не меняется)."),
        ("Окно и значок в трее", "Крестик только прячет окно — обои продолжают работать. Значок Wallsets в трее возле часов открывает окно и меню. «Отключить и выйти» в настройках (или в меню значка) останавливает обои и возвращает обычный фон Windows."),
        ("Настройки", "Автозапуск вместе с Windows, масштаб фона (по размеру, растянуть, заполнение, замостить), звук, экономия энергии (пауза, когда рабочий стол закрыт развёрнутым окном), ярлыки на рабочий стол, горячие клавиши и «Отключить и выйти». Изменения применяются сразу."),
        ("Область обоев", "В настройках можно показать обои не на весь экран, а в его части: задайте координаты верхней, нижней, левой и правой сторон в пикселях. «Показать область текущих обоев» рисует рамку по её краям, рамка и обои меняются сразу. «Вернуть обои на весь экран» убирает область, а «Вернуть редактируемые обои» возвращает прежние значения. «Показать, как обои обрезаются» открывает уменьшенную схему: картинка целиком, экран и область, обрезанное — красным."),
        ("Горячие клавиши", "По умолчанию: Ctrl+Alt+P — пауза, Ctrl+Alt+N — следующие, Ctrl+Alt+B — предыдущие, Ctrl+Alt+M — звук. Их можно поменять в настройках; Wallsets подскажет, если сочетание занято другой программой."),
        ("Если что-то не так", "Если при первом запуске Windows показала «Windows защитила ваш компьютер», нажмите «Подробнее» → «Выполнить в любом случае». Держите Wallsets.exe вместе с папками engine и «Наборы». Ошибки записываются в файл errors.log рядом с программой."),
    ];

    public HelpForm(Icon? icon)
    {
        Text = "Справка · Wallsets"; Icon = icon; Font = Ui.Body; BackColor = Ui.Window; ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        int cardWidth = Ui.S(520);
        ClientSize = new Size(cardWidth + Ui.S(40) + SystemInformation.VerticalScrollBarWidth, Math.Min(Ui.S(660), Screen.PrimaryScreen!.WorkingArea.Height - Ui.S(100)));

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Window };
        var card = new Card { Location = new Point(Ui.S(20), Ui.S(16)), Width = cardWidth };
        int y = Ui.S(16), left = Ui.S(20), width = cardWidth - Ui.S(40);
        Label Add(string text, Font font, Color color, int gap)
        {
            var label = new Label { Text = text, Font = font, ForeColor = color, BackColor = Ui.Surface, AutoSize = false, UseMnemonic = false, Location = new Point(left, y) };
            label.Size = new Size(width, TextRenderer.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak).Height + Ui.S(2));
            card.Controls.Add(label); y += label.Height + Ui.S(gap);
            return label;
        }
        foreach (var (title, text) in Topics)
        {
            Add(title, Ui.Strong, Ui.Text, 2);
            Add(text, Ui.Body, Ui.Muted, 14);
        }
        card.Height = y;
        content.Controls.Add(card);
        content.Controls.Add(new Panel { Location = new Point(0, card.Bottom), Size = new Size(1, Ui.S(16)), BackColor = Ui.Window });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(60), BackColor = Ui.Window };
        var ok = Ui.TextButton("Понятно", Close, accent: true);
        footer.Controls.Add(ok);
        footer.Layout += (_, _) => ok.Location = new Point(footer.ClientSize.Width - ok.Width - Ui.S(20), (footer.Height - ok.Height) / 2);
        Controls.Add(content); Controls.Add(footer);
        AcceptButton = ok; CancelButton = ok;
    }
}
