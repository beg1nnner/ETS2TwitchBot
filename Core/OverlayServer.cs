using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace EtsBot;

/// <summary>
/// Локальный веб-сервер для OBS: источник «Браузер» с адресом http://localhost:ПОРТ/donations
/// показывает подсказки «что можно сделать за донат» по правилам из программы.
/// Страница сама обновляется, поэтому после правки правил в OBS ничего трогать не нужно.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    private readonly AppSettings _s;
    private HttpListener? _listener;

    public int Port { get; private set; }
    public string? Error { get; private set; }
    public bool Running => _listener?.IsListening == true;
    private string Base => $"http://localhost:{(Port > 0 ? Port : _s.General.OverlayPort)}";
    public string Url => Base + "/donations";
    public string AlertsUrl => Base + "/alerts";
    public string TopUrl => Base + "/top";
    public string VoteUrl => Base + "/vote";
    public string GoalUrl => Base + "/goal";

    public OverlayServer(AppSettings s) { _s = s; }

    public void Restart()
    {
        Stop();
        if (!_s.General.OverlayEnabled) return;
        Start();
    }

    private void Start()
    {
        Error = null;
        Port = _s.General.OverlayPort;
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://localhost:{Port}/");
            l.Start();
            _listener = l;
            Task.Run(() => LoopAsync(l));
            Log.Ok("Оверлей", "Запущен: " + Url);
        }
        catch (Exception ex)
        {
            _listener = null;
            Error = ex is HttpListenerException
                ? $"порт {Port} занят или недоступен — выбери другой порт"
                : ex.Message;
            Log.Error("Оверлей", "Не удалось запустить: " + Error);
        }
    }

    public void Stop()
    {
        var l = _listener;
        _listener = null;
        if (l == null) return;
        try { l.Close(); } catch { /* ignore */ }
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            string body, type;
            int status = 200;
            switch (path)
            {
                case "":
                case "/donations":
                    body = PageHtml; type = "text/html; charset=utf-8"; break;
                case "/data":
                    body = BuildJson(); type = "application/json; charset=utf-8"; break;
                case "/alerts":
                    body = AlertsHtml; type = "text/html; charset=utf-8"; break;
                case "/alert-data":
                    body = JsonSerializer.Serialize(OverlayState.Snapshot(), JsonOpt); type = "application/json; charset=utf-8"; break;
                case "/top":
                    body = TopHtml; type = "text/html; charset=utf-8"; break;
                case "/vote":
                    body = VoteHtml; type = "text/html; charset=utf-8"; break;
                case "/vote-data":
                    body = JsonSerializer.Serialize(VoteState.Snapshot(), JsonOpt); type = "application/json; charset=utf-8"; break;
                case "/goal":
                    body = GoalHtml; type = "text/html; charset=utf-8"; break;
                case "/goal-data":
                    body = JsonSerializer.Serialize(new
                    {
                        title = _s.General.PiggyReward.Length > 0 ? "Копилка зрителей" : "",
                        amount = PiggyStore.Instance.Amount,
                        goal = _s.General.PiggyGoal,
                    }, JsonOpt);
                    type = "application/json; charset=utf-8"; break;
                case "/top-data":
                    body = BuildTopJson(ctx.Request.QueryString["mode"], ctx.Request.QueryString["n"]);
                    type = "application/json; charset=utf-8"; break;
                default:
                    body = "not found"; type = "text/plain; charset=utf-8"; status = 404; break;
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch { /* клиент закрыл соединение — не страшно */ }
        finally { try { ctx.Response.Close(); } catch { /* ignore */ } }
    }

    // ---------------------------------------------------------------- данные

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string Sym(string cur) => cur.ToUpperInvariant() switch
    {
        "RUB" => "₽", "USD" => "$", "EUR" => "€", "UAH" => "₴", "KZT" => "₸", "BYN" => "Br", "TRY" => "₺",
        var c => c,
    };

    private static string Num(double v) => v.ToString("#,0.##", Ru).Replace(' ', ' ');

    public static string AmountText(DonationRule r, string cur)
    {
        var sym = Sym(cur);
        if (r.MinAmount.HasValue && r.MaxAmount.HasValue) return $"{Num(r.MinAmount.Value)}–{Num(r.MaxAmount.Value)} {sym}";
        if (r.MinAmount.HasValue) return $"от {Num(r.MinAmount.Value)} {sym}";
        if (r.MaxAmount.HasValue) return $"до {Num(r.MaxAmount.Value)} {sym}";
        return "любая сумма";
    }

    public static string? ExtraText(DonationRule r, string cur)
    {
        if (r.PerAmount.HasValue && r.PerAmount.Value > 0)
            return $"Каждые {Num(r.PerAmount.Value)} {Sym(cur)} — ещё раз (до {r.MaxTimes})";
        return null;
    }

    public string BuildJson()
    {
        var g = _s.General;
        var cur = _s.Donations.BaseCurrency;

        DonationRule[] rules;
        try { rules = _s.Donations.Rules.ToArray(); }
        catch { rules = Array.Empty<DonationRule>(); } // правки в этот момент — на следующем опросе всё придёт

        var items = new List<object>();
        foreach (var r in rules
                     .Where(x => x.Enabled && x.ShowOnOverlay)
                     .OrderBy(x => x.MinAmount ?? 0))
        {
            string name = r.Name, hint = r.Hint.Trim();
            if (!string.IsNullOrWhiteSpace(r.Reward))
            {
                var rw = _s.FindReward(r.Reward);
                if (rw != null)
                {
                    name = rw.Title;
                    if (hint.Length == 0) hint = rw.Prompt.Trim();
                }
            }
            var words = r.Keywords.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            items.Add(new
            {
                name,
                hint,
                amount = AmountText(r, cur),
                extra = ExtraText(r, cur),
                keywords = words.Length > 0 ? "В сообщении: " + string.Join(" / ", words) : "Сообщение любое",
            });
        }

        return JsonSerializer.Serialize(new
        {
            title = g.OverlayTitle,
            rows = g.OverlayRows,
            seconds = g.OverlaySeconds,
            items,
        }, JsonOpt);
    }

    public static string BuildTopJson(string? mode, string? count)
    {
        bool session = !string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase);
        int n = int.TryParse(count, out var c) && c > 0 && c <= 20 ? c : 5;
        var top = StatsStore.Instance.Top(session, n)
            .Select(t => new { name = t.Name, count = t.Count }).ToList();
        return JsonSerializer.Serialize(new { session, items = top }, JsonOpt);
    }

    // ---------------------------------------------------------------- страницы

    private const string AlertsHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Алерты</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { background: transparent; overflow: hidden; font-family: "Segoe UI", system-ui, sans-serif; color: #f4f6fc; }
  #card { margin: 16px; padding: 16px 22px 14px; border-radius: 20px; opacity: 0; transform: translateY(-30px) scale(.96);
          background: rgba(14,16,28,.86); border: 1px solid rgba(255,255,255,.12); box-shadow: 0 10px 34px rgba(0,0,0,.5);
          transition: opacity .35s ease, transform .35s cubic-bezier(.2,.8,.2,1); }
  #card.show { opacity: 1; transform: none; }
  .who { font-size: 20px; color: #c4b3ff; font-weight: 600; }
  .what { font-size: 38px; font-weight: 800; line-height: 1.1; margin-top: 2px; text-shadow: 0 2px 10px rgba(0,0,0,.6); }
  .src { font-size: 15px; color: #a9b2cc; margin-top: 4px; }
  .bar { height: 10px; border-radius: 6px; background: rgba(255,255,255,.12); margin-top: 12px; overflow: hidden; }
  .fill { height: 100%; width: 100%; border-radius: 6px; background: linear-gradient(90deg, #7c5cff, #27c3ff); transition: width .4s linear; }
</style>
</head>
<body>
<div id="card">
  <div class="who" id="who"></div>
  <div class="what" id="what"></div>
  <div class="src" id="src"></div>
  <div class="bar"><div class="fill" id="fill"></div></div>
</div>
<script>
(function () {
  var shown = 0;
  var card = document.getElementById('card');
  function poll() {
    fetch('/alert-data', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (d.active) {
          if (d.id !== shown) {
            shown = d.id;
            document.getElementById('who').textContent = d.user ? d.user + ' запускает' : 'Запускается';
            document.getElementById('what').textContent = d.title;
            document.getElementById('src').textContent = d.source + ' · ' + Math.round(d.duration) + ' сек';
            card.className = 'show';
          }
          var pct = d.duration > 0 ? Math.max(0, Math.min(100, d.remaining / d.duration * 100)) : 0;
          document.getElementById('fill').style.width = pct + '%';
        } else if (shown !== 0) {
          shown = 0;
          card.className = '';
        }
      })
      .catch(function () { });
  }
  setInterval(poll, 400);
  poll();
})();
</script>
</body>
</html>
""";

    private const string VoteHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Голосование</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { background: transparent; overflow: hidden; font-family: "Segoe UI", system-ui, sans-serif; color: #f4f6fc; }
  #box { margin: 14px; padding: 14px 18px; border-radius: 18px; background: rgba(14,16,28,.86); border: 1px solid rgba(255,255,255,.12);
         opacity: 0; transition: opacity .3s; }
  #box.show { opacity: 1; }
  .head { font-size: 24px; font-weight: 800; margin-bottom: 10px; display: flex; justify-content: space-between; }
  .opt { position: relative; margin-bottom: 8px; border-radius: 12px; overflow: hidden; background: rgba(255,255,255,.08); font-size: 21px; }
  .fill { position: absolute; left: 0; top: 0; bottom: 0; width: 0; background: linear-gradient(90deg, #7c5cff, #27c3ff); opacity: .75; transition: width .4s; }
  .txt { position: relative; display: flex; justify-content: space-between; padding: 9px 14px; font-weight: 600; }
  .win .txt { color: #7fe0b0; }
  .win { outline: 3px solid #7fe0b0; }
  .bar { height: 8px; border-radius: 5px; background: rgba(255,255,255,.12); margin-top: 8px; overflow: hidden; }
  .bar div { height: 100%; width: 100%; background: #27c3ff; transition: width .4s linear; }
</style>
</head>
<body>
<div id="box">
  <div class="head"><span>🗳 Голосование чата</span><span id="time"></span></div>
  <div id="opts"></div>
  <div class="bar"><div id="bar"></div></div>
</div>
<script>
(function () {
  var box = document.getElementById('box'), opts = document.getElementById('opts');
  function poll() {
    fetch('/vote-data', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d.visible) { box.className = ''; return; }
        box.className = 'show';
        var total = 0;
        d.options.forEach(function (o) { total += o.count; });
        opts.innerHTML = '';
        d.options.forEach(function (o) {
          var row = document.createElement('div');
          row.className = 'opt' + (!d.active && o.title === d.winner ? ' win' : '');
          var f = document.createElement('div'); f.className = 'fill'; f.style.width = (total ? o.count / total * 100 : 0) + '%';
          var t = document.createElement('div'); t.className = 'txt';
          var a = document.createElement('span'); a.textContent = '!' + o.n + '  ' + o.title;
          var b = document.createElement('span'); b.textContent = o.count;
          t.appendChild(a); t.appendChild(b); row.appendChild(f); row.appendChild(t); opts.appendChild(row);
        });
        document.getElementById('time').textContent = d.active ? Math.ceil(d.remaining) + ' с' : 'готово';
        document.getElementById('bar').style.width = (d.active && d.duration > 0 ? d.remaining / d.duration * 100 : 0) + '%';
      })
      .catch(function () { });
  }
  setInterval(poll, 500);
  poll();
})();
</script>
</body>
</html>
""";

    private const string GoalHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Копилка</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { background: transparent; overflow: hidden; font-family: "Segoe UI", system-ui, sans-serif; color: #f4f6fc; }
  #box { margin: 14px; padding: 14px 18px; border-radius: 18px; background: rgba(14,16,28,.86); border: 1px solid rgba(255,255,255,.12); }
  .head { font-size: 24px; font-weight: 800; display: flex; justify-content: space-between; margin-bottom: 10px; }
  .bar { height: 26px; border-radius: 14px; background: rgba(255,255,255,.12); overflow: hidden; }
  .fill { height: 100%; width: 0; border-radius: 14px; background: linear-gradient(90deg, #f5a623, #ff6b6b); transition: width .6s ease; }
  .sub { margin-top: 8px; color: #a9b2cc; font-size: 16px; }
</style>
</head>
<body>
<div id="box">
  <div class="head"><span id="title">💰 Копилка</span><span id="nums"></span></div>
  <div class="bar"><div class="fill" id="fill"></div></div>
  <div class="sub">Накопим цель — стримера ждёт мега-хаос!</div>
</div>
<script>
(function () {
  function poll() {
    fetch('/goal-data', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        document.getElementById('box').style.display = d.title ? 'block' : 'none';
        document.getElementById('nums').textContent = d.amount + ' / ' + d.goal;
        document.getElementById('fill').style.width = Math.min(100, d.goal > 0 ? d.amount / d.goal * 100 : 0) + '%';
      })
      .catch(function () { });
  }
  setInterval(poll, 2000);
  poll();
})();
</script>
</body>
</html>
""";

    private const string TopHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Топ мучителей</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { background: transparent; overflow: hidden; font-family: "Segoe UI", system-ui, sans-serif; color: #f4f6fc; }
  #wrap { padding: 14px; }
  .head { font-size: 28px; font-weight: 800; margin-bottom: 10px; text-shadow: 0 2px 10px rgba(0,0,0,.7); }
  .row { display: flex; align-items: center; gap: 14px; padding: 10px 16px; margin-bottom: 8px; border-radius: 14px;
         background: rgba(14,16,28,.82); border: 1px solid rgba(255,255,255,.10); font-size: 24px; }
  .pos { width: 40px; text-align: center; font-size: 26px; }
  .name { flex: 1; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .cnt { font-weight: 800; padding: 4px 12px; border-radius: 10px; background: linear-gradient(135deg, #7c5cff, #27c3ff); }
  .empty { color: #a9b2cc; font-size: 20px; padding: 12px; }
</style>
</head>
<body>
<div id="wrap"><div class="head" id="title"></div><div id="list"></div></div>
<script>
(function () {
  var q = new URLSearchParams(location.search);
  var mode = q.get('mode') === 'all' ? 'all' : 'session';
  var n = parseInt(q.get('n') || '5', 10) || 5;
  var medals = ['🥇', '🥈', '🥉'];
  var list = document.getElementById('list');
  function load() {
    fetch('/top-data?mode=' + mode + '&n=' + n, { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        document.getElementById('title').textContent = '😈 Топ мучителей' + (mode === 'all' ? ' (за всё время)' : ' (за стрим)');
        list.innerHTML = '';
        if (!d.items.length) {
          var e = document.createElement('div'); e.className = 'empty'; e.textContent = 'Пока никто не мучил стримера';
          list.appendChild(e); return;
        }
        d.items.forEach(function (it, i) {
          var row = document.createElement('div'); row.className = 'row';
          var pos = document.createElement('div'); pos.className = 'pos'; pos.textContent = medals[i] || (i + 1);
          var nm = document.createElement('div'); nm.className = 'name'; nm.textContent = it.name;
          var c = document.createElement('div'); c.className = 'cnt'; c.textContent = it.count;
          row.appendChild(pos); row.appendChild(nm); row.appendChild(c);
          list.appendChild(row);
        });
      })
      .catch(function () { });
  }
  load();
  setInterval(load, 5000);
})();
</script>
</body>
</html>
""";

    // ---------------------------------------------------------------- страница подсказок

    private const string PageHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Подсказки по донатам</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
  :root { --bg: rgba(14,16,28,.82); --line: rgba(255,255,255,.10); --text: #f4f6fc; --muted: #a9b2cc; --kw: #c4b3ff; --a1: #7c5cff; --a2: #27c3ff; }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  html, body { background: transparent; overflow: hidden; font-family: "Segoe UI", system-ui, sans-serif; color: var(--text); }
  #wrap { width: 100%; padding: 14px; }
  .head { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; font-size: 28px; font-weight: 800;
          letter-spacing: .2px; text-shadow: 0 2px 10px rgba(0,0,0,.7); }
  .head .ic { width: 38px; height: 38px; border-radius: 12px; display: grid; place-items: center; font-size: 22px;
              background: linear-gradient(135deg, var(--a1), var(--a2)); box-shadow: 0 0 22px rgba(124,92,255,.55); }
  .list { display: flex; flex-direction: column; gap: 10px; }
  .card { display: grid; grid-template-columns: 150px 1fr; gap: 16px; align-items: center; padding: 12px 16px;
          background: var(--bg); border: 1px solid var(--line); border-radius: 16px; box-shadow: 0 6px 22px rgba(0,0,0,.35);
          animation: in .55s cubic-bezier(.2,.8,.2,1) both; animation-delay: calc(var(--i) * 90ms); }
  .out .card { animation: out .35s ease-in both; animation-delay: calc(var(--i) * 40ms); }
  .amt { text-align: center; padding: 10px 8px; border-radius: 12px; font-weight: 800; font-size: 22px; line-height: 1.15;
         background: linear-gradient(135deg, var(--a1), var(--a2)); text-shadow: 0 1px 4px rgba(0,0,0,.35); }
  .name { font-size: 24px; font-weight: 700; line-height: 1.2; }
  .hint { font-size: 17px; color: var(--muted); margin-top: 2px; line-height: 1.25; }
  .kw { font-size: 16px; color: var(--kw); margin-top: 4px; }
  .extra { font-size: 15px; color: #7fe0b0; margin-top: 3px; }
  .dots { display: flex; justify-content: center; gap: 7px; margin-top: 12px; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: rgba(255,255,255,.28); transition: all .3s; }
  .dot.on { width: 22px; border-radius: 6px; background: linear-gradient(90deg, var(--a1), var(--a2)); }
  .empty { padding: 20px; text-align: center; color: var(--muted); font-size: 20px; background: var(--bg); border-radius: 16px; }
  @keyframes in  { from { opacity: 0; transform: translateX(-28px) scale(.98); } to { opacity: 1; transform: none; } }
  @keyframes out { from { opacity: 1; transform: none; } to { opacity: 0; transform: translateX(28px); } }
</style>
</head>
<body>
<div id="wrap">
  <div class="head"><div class="ic">💸</div><div id="title"></div></div>
  <div id="list" class="list"></div>
  <div id="dots" class="dots"></div>
</div>
<script>
(function () {
  var q = new URLSearchParams(location.search);
  var data = null, page = 0, timer = null, lastJson = '';
  var $list = document.getElementById('list');
  var $title = document.getElementById('title');
  var $dots = document.getElementById('dots');

  function rows() { var r = parseInt(q.get('rows') || (data && data.rows) || 6, 10); return r > 0 ? r : 6; }
  function secs() { var s = parseFloat(q.get('seconds') || (data && data.seconds) || 8); return s >= 2 ? s : 8; }
  function pages() { return data && data.items.length ? Math.ceil(data.items.length / rows()) : 1; }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text) e.textContent = text;
    return e;
  }

  function draw() {
    $title.textContent = data.title || '';
    $title.parentNode.style.display = data.title ? 'flex' : 'none';
    $list.className = 'list';
    $list.innerHTML = '';
    if (!data.items.length) {
      $list.appendChild(el('div', 'empty', 'Правил для донатов пока нет'));
      $dots.innerHTML = '';
      return;
    }
    var n = rows(), start = page * n;
    data.items.slice(start, start + n).forEach(function (it, i) {
      var card = el('div', 'card');
      card.style.setProperty('--i', i);
      card.appendChild(el('div', 'amt', it.amount));
      var body = el('div');
      body.appendChild(el('div', 'name', it.name));
      if (it.hint) body.appendChild(el('div', 'hint', it.hint));
      body.appendChild(el('div', 'kw', it.keywords));
      if (it.extra) body.appendChild(el('div', 'extra', it.extra));
      card.appendChild(body);
      $list.appendChild(card);
    });
    $dots.innerHTML = '';
    var p = pages();
    if (p > 1) for (var k = 0; k < p; k++) $dots.appendChild(el('div', 'dot' + (k === page ? ' on' : '')));
  }

  function next() {
    if (!data) return;
    if (pages() <= 1) return;
    $list.className = 'list out';
    setTimeout(function () { page = (page + 1) % pages(); draw(); }, 420);
  }

  function arm() {
    if (timer) clearInterval(timer);
    timer = setInterval(next, secs() * 1000);
  }

  function load() {
    fetch('/data', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        var j = JSON.stringify(d);
        if (j === lastJson) return;
        lastJson = j; data = d;
        if (page >= pages()) page = 0;
        draw(); arm();
      })
      .catch(function () { /* программа закрыта — оставляем что было */ });
  }

  load();
  setInterval(load, 4000);
})();
</script>
</body>
</html>
""";
}
