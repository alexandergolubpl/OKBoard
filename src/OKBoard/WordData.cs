namespace OKBoard;

/// <summary>
/// Built-in frequency word lists. They act as a fallback dictionary when the Windows spell checker
/// is unavailable and as training data for the bigram model that judges unknown words.
/// </summary>
internal static class WordData
{
    public const string Ru = """
        и в не на я быть он с что а по это она этот к но они мы как из у который то за свой что весь год от
        так о для ты же все тот мочь вы человек такой его сказать только или еще бы себя один как уже до время
        если сам когда другой вот говорить наш мой знать стать при чтобы дело жизнь кто первый очень два день
        ее новый рука даже во со раз где там под можно ну какой после их работа без самый потом надо хотеть ли
        слово идти большой должен место иметь ничто то сейчас тут лицо каждый друг нет теперь ни глаз тоже тогда
        видеть вопрос через да здесь дом да потому сторона какой-то думать сделать страна жить чем мир об последний
        случай голова более делать что-то смотреть ребенок просто конечно сила российский конец перед несколько вид
        система всегда работать между три нет понять пойти часть спросить город дать также никто понимать получить
        отношение лишь второй именно значит ведь вдруг давать какой-нибудь хорошо привет пока спасибо пожалуйста
        здравствуйте добрый утро вечер ночь сегодня завтра вчера сейчас скоро быстро медленно хороший плохой
        большой маленький красивый новый старый молодой правильно неправильно точно может быть наверное вообще
        почему зачем куда откуда сколько который чей всё ещё уже было была были будет будут буду будем есть нужно
        нужен нужна можно нельзя надо хочу хочешь хочет хотим хотите хотят могу можешь может можем можете могут
        знаю знаешь знает знаем думаю думаешь думает делаю делаешь делает сделал сделала сделали сказал сказала
        говорю говоришь говорит пишу пишешь пишет написал написала читаю читает работаю работает работал
        вижу видишь видит видел видела смотрю смотрит иду идёт идет пошёл пошел пошла пришёл пришел пришла
        люблю любишь любит любовь мама папа брат сестра семья дети друзья подруга коллега начальник проект задача
        письмо сообщение ответ вопрос встреча звонок телефон компьютер интернет сайт программа файл папка документ
        текст строка код ошибка проблема решение результат время минута час неделя месяц деньги цена рубль
        рублей тысяча миллион процент компания клиент заказ договор счёт счет оплата доставка товар магазин
        сделать посмотреть проверить отправить написать позвонить купить продать открыть закрыть начать закончить
        мне меня мной тебе тебя тобой ему него нему нам нас вам вас им ими них нее неё ней свою своей своих
        этого этом этой эти этих эту тому того тем той те тех всего всех всем весь вся всю всеми
        очень много мало больше меньше лучше хуже сразу всегда никогда иногда часто редко опять снова
        тоже также даже только именно почти совсем вместе отдельно вместо около после перед между внутри
        например кстати впрочем однако поэтому потому поскольку хотя либо будто словно ладно окей хорошо
        понятно согласен согласна конечно наверно действительно просто вообще совершенно абсолютно
        спс пжлст норм баг баги фича мерж коммит пуш релиз деплой прод тест тесты кек лол ок окей
        """;

    public const string En = """
        the be to of and a in that have i it for not on with he as you do at this but his by from they we say
        her she or an will my one all would there their what so up out if about who get which go me when make
        can like time no just him know take people into year your good some could them see other than then now
        look only come its over think also back after use two how our work first well way even new want because
        any these give day most us is are was were been has had did does said made went got thing things many
        much more very really here where why how hello hi hey thanks thank please yes yeah okay ok sorry sure
        today tomorrow yesterday morning evening night week month should must might may need needs needed
        help call email message meeting project task team manager code file folder document text line error
        problem issue fix bug test build run start stop open close send read write update check review deploy
        server client user data value list name number string table page site web app application system
        world life hand part child eye woman man place case point government company group question fact
        right left high long great little own old big small large next early young important few public bad
        same able last late hard free full best better sure clear real true whole love home house car money
        business service price order customer product market report information example result change
        always never sometimes often again still already yet soon maybe probably actually just only also
        before after during while until since without within between through under above below around
        something nothing everything anything someone anyone everyone nobody which whose whom what whatever
        going doing being having making taking looking coming working using trying getting saying thinking
        let tell ask feel try leave put mean keep begin seem show hear play move live believe bring happen
        provide sit stand lose pay meet include continue set learn lead understand watch follow create speak
        spend grow offer remember consider appear buy wait serve die send expect stay fall reach kill remain
        suggest raise pass sell require decide return explain hope develop carry break receive agree support
        hit produce eat cover catch draw choose cause point listen talk walk turn find give tell work call
        get set new var let int bool void null true false class public private static return async await
        api url http https html css json yaml xml sql git npm pip node python java docker kubectl dev prod
        repo merge commit push pull branch release deploy config admin login logout password localhost
        lol btw omg thx pls imho asap fyi gonna wanna gotta
        """;

    public const string RuMore = """
        привет приветик здравствуй здравствуйте спасибо пожалуйста пока хорошо отлично нормально норм понятно ясно
        конечно наверное возможно сегодня завтра вчера сейчас щас потом сначала давай давайте можно нужно надо
        хочу хочешь хочет хотел хотела можешь может могу смогу сможешь сделаю сделаешь сделал сделала сделали
        делаю делаешь делает думаю думаешь знаю знаешь помню помнишь вижу видишь пишу пишешь напиши напишу
        позвони позвоню звоню звонил звонила скажи скажу сказал сказала говорю говоришь посмотри посмотрю
        смотрю смотрел работаю работает работал работала работу работы занят занята свободен свободна
        встреча встречу встрече созвон созвонимся звонок письмо сообщение ответ ответь ответил вопрос вопросы
        проблема проблемы задача задачи задачу проект проекта проекты решение готово готов готова сделано
        проверь проверю проверил проверила посмотрел посмотрела отправь отправлю отправил отправила получил
        получила скинь скину скинул файл файлы папка ссылка ссылку ссылки документ документы таблица таблицу
        коммит коммиты мерж мержу смержил пулл пуш запушил релиз релизы деплой задеплоил билд билды сборка
        сборку баг баги багов фича фичи фичу тикет тикеты таска таски тест тесты тестирую ревью фидбек дедлайн
        митинг митинга стендап спринт бэклог продакшн прод сервер сервера база базы данные код кода коде
        программа программы прога компьютер комп ноутбук телефон интернет сайт браузер приложение приложения
        настройки обновление версия версии ошибка ошибки ошибку работает сломалось починил починила
        кстати короче вообще вроде типа просто очень слишком совсем почти уже еще ещё тоже также только
        всегда никогда иногда часто редко сразу быстро медленно долго скоро поздно рано утром вечером ночью
        неделя неделю неделе месяц месяца год года день дня дней часа часов минут минуту секунду
        хорошего доброе утро добрый день вечер спокойной ночи увидимся пожалуйста извини извините прости
        согласен согласна отлично супер класс круто классно здорово ладно окей ок спс плз имхо лол кек
        человек люди друг друзья брат сестра мама папа семья дети ребенок дом домой работе офис офисе
        деньги цена стоимость оплата оплатить счет счёт заказ заказа доставка магазин купить продать
        """;

    public const string EnMore = """
        hello hi hey thanks thank please sorry okay yes yeah yep nope sure maybe today tomorrow yesterday
        tonight morning evening night week month year time minute hour second soon later now still already
        just only also very really quite actually basically probably definitely maybe almost always never
        sometimes often again together meeting call email message reply question answer problem issue task
        project file folder link document table sheet report update version release build deploy fix bug
        feature ticket review feedback deadline sprint standup backlog server database data code test tests
        testing debug error errors warning crash works working broken fixed done ready check checked send
        sent receive received download upload install settings setting option options window windows screen
        keyboard mouse layout language english russian ukrainian switch switcher please help need want know
        think see look looking going doing make making made thing things stuff great good nice cool awesome
        perfect fine right wrong true false correct
        vs ye eg ie js ts db ip os pc pr qa ui ux ctx cfg evt rect gti yyyy msg btn img src dst tmp idx len str obj
        num arr res req ptr buf fmt pos dir env ext ref repo impl init util utils param params arg args func proc
        ctl lbl txt pic doc docs spec specs sync async enum bool const readonly nullable hwnd hkl vk dpi rgb argb
        """;

    public const string Uk = """
        і в не на я бути він з що а по це вона цей до але вони ми як із у який те за свій весь рік від так
        про для ти же все той могти ви людина такий його сказати тільки або ще би себе один вже коли час якщо
        сам інший ось говорити наш мій знати стати при щоб справа життя хто перший дуже два день її новий рука
        навіть раз де там під можна ну після їх робота без самий потім треба хотіти слово йти великий місце мати
        нічого зараз тут обличчя кожен друг ні тепер око теж тоді бачити питання через так тут будинок країна
        жити світ останній випадок голова більше робити дивитися дитина просто звичайно сила кінець перед кілька
        система завжди працювати між три зрозуміти піти частина спитати місто дати також ніхто розуміти отримати
        лише другий саме значить адже раптом добре привіт поки дякую будь ласка доброго ранку вечір ніч сьогодні
        завтра вчора скоро швидко повільно гарний поганий маленький красивий старий молодий правильно точно
        можливо мабуть взагалі чому навіщо куди звідки скільки чий було була були буде будуть буду будемо є
        потрібно потрібен потрібна не можна хочу хочеш хоче хочемо хочете хочуть можу можеш може можемо можете
        можуть знаю знаєш знає знаємо думаю думаєш думає роблю робиш робить зробив зробила зробили сказав сказала
        кажу кажеш каже пишу пишеш пише написав написала читаю читає працюю працює працював бачу бачиш бачить
        бачив бачила дивлюся іду іде йде пішов пішла прийшов прийшла люблю любиш любить кохання мама тато брат
        сестра родина діти друзі подруга колега керівник проєкт проект завдання лист повідомлення відповідь
        зустріч дзвінок телефон комп'ютер інтернет сайт програма файл папка документ текст рядок код помилка
        проблема рішення результат хвилина година тиждень місяць гроші ціна гривня гривень тисяча мільйон
        відсоток компанія клієнт замовлення договір рахунок оплата доставка товар магазин подивитися перевірити
        відправити написати подзвонити купити продати відкрити закрити почати закінчити мені мене мною тобі тебе
        тобою йому нього нам нас вам вас їм ними них неї ній свою своєї своїх цього цьому цієї ці цих цю того тим
        тієї ті тих всього всіх всім вся всю усі усе дуже багато мало більш менше краще гірше одразу ніколи іноді
        часто рідко знову разом окремо замість біля всередині наприклад до речі втім однак тому бо оскільки хоча
        чи ніби начебто гаразд окей зрозуміло згоден згодна дійсно цілком абсолютно україна український київ
        мова слава дякуємо вибач вибачте будь-ласка звісно сонце вода хліб молоко кава чай школа університет
        """;

    public static readonly HashSet<string> Uk2 =
        ["не", "на", "по", "та", "це", "як", "що", "до", "за", "він", "ми", "ви", "ти", "їх", "її", "бо", "чи", "же", "ще", "із", "зі", "ні", "ну", "так", "ок", "от", "би", "аж", "де"];

    /// <summary>Two-letter words that may be auto-corrected (spell checkers accept far too many 2-letter strings).</summary>
    public static readonly HashSet<string> Ru2 =
        ["не", "на", "по", "да", "но", "он", "мы", "вы", "ты", "же", "бы", "из", "за", "до", "от", "то", "их", "ее", "её", "ли", "уж", "во", "со", "ну", "ни", "мне", "об", "ей", "им", "ко", "ок"];

    public static readonly HashSet<string> En2 =
        ["is", "in", "on", "at", "to", "it", "of", "be", "we", "he", "me", "my", "an", "as", "by", "do", "go", "if", "no", "or", "so", "up", "us", "am", "hi", "ok"];
}
