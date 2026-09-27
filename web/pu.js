/* 噗~噗噗~~噗噗噗噗~~~~ 网页公共脚本（播放页 / 文件夹页共用，/assets/pu.js）
   不用构建工具：老 Safari 也要能跑，所以写成 ES5 风格（var / function），不用可选链等新语法。 */
(function(){
  'use strict';
  var reduced = !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);

  function $(id){ return document.getElementById(id); }

  /* localStorage 包一层：隐私模式 / 禁用存储时读写会抛异常，页面照常工作只是不记进度 */
  var store = {
    get: function(k){ try { return localStorage.getItem(k); } catch (e) { return null; } },
    set: function(k, v){ try { localStorage.setItem(k, v); } catch (e) {} },
    del: function(k){ try { localStorage.removeItem(k); } catch (e) {} }
  };

  /* 设备类别报给服务端（电脑窗口显示「送到了 iPad」）。
     iPadOS 13+ Safari 默认伪装成 Mac，UA 里只有 Macintosh，要靠触摸点数认出来 */
  function device(){
    var ua = navigator.userAgent || '';
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'ipad';
    if (/iPhone|iPod/.test(ua)) return 'iphone';
    if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'android-phone' : 'android-tablet';
    if (/Macintosh/.test(ua)) return 'mac';
    if (/Windows/.test(ua)) return 'windows';
    return 'other';
  }

  function once(url){
    var p = null;
    return function(){
      if (!p) p = fetch(url).then(function(r){ if (!r.ok) throw new Error(r.status); return r; });
      return p;
    };
  }
  var svgText = once('/assets/mascot.svg');
  var wordsJson = once('/assets/words.json');
  var words = function(){ return wordsJson().then(function(r){ return r.clone().json(); }); };

  /* 在容器里放一只噗噗，返回 { set(face) }。SVG 内联进页面，颜色和动画才能吃到页面 CSS */
  function mascot(el, face){
    var svg = null, want = face || 'idle';
    svgText().then(function(r){ return r.clone().text(); }).then(function(text){
      el.innerHTML = text;
      svg = el.querySelector('svg');
      svg.setAttribute('aria-hidden', 'true');
      svg.setAttribute('data-face', want);
    }).catch(function(){});
    return { set: function(f){ want = f; if (svg) svg.setAttribute('data-face', f); } };
  }

  /* 噗噗的气泡：say(台词数组) 循环轮换；hide() 收起 */
  function bubble(el){
    var span = document.createElement('span');
    el.appendChild(span);
    var timer = null, key = '';
    function stop(){ if (timer){ clearInterval(timer); timer = null; } }
    function swap(text){
      if (reduced){ span.textContent = text; return; }
      span.classList.add('swap');
      setTimeout(function(){ span.textContent = text; span.classList.remove('swap'); }, 280);
    }
    return {
      say: function(lines, ms){
        var k = lines.join('|');
        el.hidden = false;
        if (k === key) return;
        key = k; stop();
        var i = Math.floor(Math.random() * lines.length);
        span.textContent = lines[i];
        if (lines.length > 1) timer = setInterval(function(){ i = (i + 1) % lines.length; swap(lines[i]); }, ms || 3000);
      },
      hide: function(){ stop(); key = ''; el.hidden = true; }
    };
  }

  /* 夸夸词：「全世界最可爱」→「全世界最可爱的噗噗大王~」 */
  function praise(w){
    return (w.praise || []).map(function(p){ return (w.praiseTemplate || '{0}').replace('{0}', p); });
  }

  /* 圆珠笔排线进度条：手绘胶囊外框 + 斜排线，按 --p 裁剪。set(null) = 还没进度（排线整条淡淡闪） */
  var hatchSeq = 0;
  function hatch(el){
    var lines = '';
    for (var x = -6, i = 0; x < 406; x += 7, i++){
      var j = (i * 37 % 5) * 0.35; // 每根线长短角度略不同，像手涂的
      lines += 'M' + (x + j).toFixed(1) + ' ' + (17 - j * .4).toFixed(1) + 'L' + (x + 9 - j).toFixed(1) + ' ' + (3 + j * .3).toFixed(1);
    }
    var frame = 'M9 1.6Q200 .3 391 1.9Q399 2.6 398.5 10Q398 18.4 390 18.3Q200 19.7 10 18.1Q1.5 17.9 1.5 10Q1.6 1.9 9 1.6Z';
    var cid = 'pu-hatch-' + (++hatchSeq); // 排线裁进手绘框里，圆头处不出界
    el.innerHTML = '<svg viewBox="0 0 400 20" preserveAspectRatio="none" aria-hidden="true">'
      + '<clipPath id="' + cid + '"><path d="' + frame + '"/></clipPath>'
      + '<g clip-path="url(#' + cid + ')"><g class="fill"><path d="' + lines + '" stroke="var(--ink)" stroke-width="2" stroke-linecap="round" fill="none" vector-effect="non-scaling-stroke"/></g></g>'
      + '<path d="' + frame + '" fill="none" stroke="var(--ink)" stroke-width="2" vector-effect="non-scaling-stroke"/>'
      + '</svg>';
    el.setAttribute('role', 'progressbar');
    el.setAttribute('aria-valuemin', '0');
    el.setAttribute('aria-valuemax', '100');
    return {
      set: function(f){
        var indet = f === null || f === undefined;
        el.classList.toggle('indet', indet);
        var pct = indet ? 0 : Math.round(f * 100);
        el.style.setProperty('--p', pct + '%');
        if (indet) el.removeAttribute('aria-valuenow'); else el.setAttribute('aria-valuenow', String(pct));
      }
    };
  }

  /* 剩余时间估算：最近 30 秒的进度速率，指数平滑防跳。前 5 秒或速率不可信时返回 null（显示「估算中」） */
  function etaEstimator(){
    var samples = [], smooth = null, t0 = Date.now();
    return function(p){
      var now = Date.now();
      samples.push([now, p]);
      while (samples.length > 2 && now - samples[0][0] > 30000) samples.shift();
      if (now - t0 < 5000 || samples.length < 2) return null;
      var dt = (now - samples[0][0]) / 1000, dp = p - samples[0][1];
      if (dp <= 0 || dt <= 0) return smooth;
      var eta = (1 - p) / (dp / dt);
      smooth = smooth === null ? eta : smooth * 0.7 + eta * 0.3;
      return smooth;
    };
  }

  function fmtEta(sec){
    if (sec === null || !isFinite(sec)) return '正在估算还要多久';
    if (sec < 50) return '马上就好';
    var m = Math.round(sec / 60);
    if (m < 60) return '还要 ' + m + ' 分钟左右';
    return '还要 ' + Math.floor(m / 60) + ' 小时 ' + (m % 60) + ' 分钟左右';
  }

  function fmtTime(sec){
    sec = Math.max(0, Math.floor(sec));
    var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    var mm = (h && m < 10 ? '0' : '') + m, ss = (s < 10 ? '0' : '') + s;
    return (h ? h + ':' : '') + mm + ':' + ss;
  }

  function fmtSize(b){
    if (b >= 1073741824) return (b / 1073741824).toFixed(1) + ' GB';
    if (b >= 1048576) return Math.round(b / 1048576) + ' MB';
    return Math.max(1, Math.round(b / 1024)) + ' KB';
  }

  /* 复制：http 局域网页面不是安全上下文，navigator.clipboard 不存在 → execCommand 兜底 */
  function copy(text){
    if (window.isSecureContext && navigator.clipboard)
      return navigator.clipboard.writeText(text).then(function(){ return true; }, function(){ return legacyCopy(text); });
    return Promise.resolve(legacyCopy(text));
  }
  function legacyCopy(text){
    var ta = document.createElement('textarea');
    ta.value = text; ta.setAttribute('readonly', '');
    ta.style.position = 'fixed'; ta.style.opacity = '0'; ta.style.top = '0';
    document.body.appendChild(ta);
    ta.select(); ta.setSelectionRange(0, text.length);
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) {}
    document.body.removeChild(ta);
    return ok;
  }

  var toastEl = null, toastTimer = null;
  /* 轻提示：可带一个动作按钮（如「从头看」） */
  function toast(msg, action, ms){
    if (!toastEl){
      toastEl = document.createElement('div');
      toastEl.className = 'toast';
      toastEl.setAttribute('role', 'status');
      document.body.appendChild(toastEl);
    }
    toastEl.innerHTML = '';
    var s = document.createElement('span'); s.textContent = msg; toastEl.appendChild(s);
    if (action){
      var b = document.createElement('button'); b.type = 'button'; b.textContent = action.label;
      b.addEventListener('click', function(){ hideToast(); action.run(); });
      toastEl.appendChild(b);
    }
    toastEl.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(hideToast, ms || 3200);
  }
  function hideToast(){ if (toastEl) toastEl.classList.remove('show'); }

  /* 分享面板：二维码 + 链接 + 复制。手机上 navigator.share 在 http 页面不可用，所以不依赖它 */
  function shareSheet(qrUrl, pageUrl){
    var back = document.createElement('div'); back.className = 'sheet-backdrop'; back.hidden = true;
    var sh = document.createElement('div'); sh.className = 'sheet'; sh.hidden = true;
    sh.setAttribute('role', 'dialog'); sh.setAttribute('aria-modal', 'true'); sh.setAttribute('aria-labelledby', 'shareTitle');
    sh.innerHTML = '<h2 id="shareTitle">让另一台设备也打开</h2>'
      + '<p>同一个 Wi-Fi 下扫码，或者把链接发过去</p>'
      + '<div class="qr-box"><img alt="当前页面的二维码"></div>'
      + '<div class="url-line"></div>'
      + '<div class="row"><button type="button" class="btn" data-close>关闭</button>'
      + '<button type="button" class="btn btn-ink" data-copy>复制链接</button></div>';
    document.body.appendChild(back); document.body.appendChild(sh);
    var img = sh.querySelector('img'), urlLine = sh.querySelector('.url-line'), last = null;
    function open(){
      var u = pageUrl();
      urlLine.textContent = u;
      img.src = qrUrl(u);
      last = document.activeElement;
      back.hidden = false; sh.hidden = false;
      requestAnimationFrame(function(){ back.classList.add('open'); sh.classList.add('open'); });
      sh.querySelector('[data-copy]').focus();
    }
    function close(){
      back.classList.remove('open'); sh.classList.remove('open');
      setTimeout(function(){ back.hidden = true; sh.hidden = true; }, reduced ? 0 : 260);
      if (last && last.focus) last.focus();
    }
    back.addEventListener('click', close);
    sh.querySelector('[data-close]').addEventListener('click', close);
    sh.querySelector('[data-copy]').addEventListener('click', function(){
      copy(urlLine.textContent).then(function(ok){
        toast(ok ? '链接已复制' : '没能自动复制，长按上面的链接手动复制');
      });
    });
    document.addEventListener('keydown', function(e){ if (e.key === 'Escape' && !sh.hidden) close(); });
    return { open: open, close: close };
  }

  /* 按状态换档的轮询：every(ms) 改周期（0 = 停），同周期重复调用不重建计时器 */
  function poller(fn){
    var timer = null, ms = 0;
    return {
      every: function(n){
        if (n === ms) return;
        if (timer){ clearInterval(timer); timer = null; }
        ms = n;
        if (n > 0) timer = setInterval(fn, n);
      },
      now: fn
    };
  }

  window.Pu = {
    $: $, store: store, device: device, reduced: reduced,
    mascot: mascot, words: words, praise: praise, bubble: bubble, hatch: hatch,
    etaEstimator: etaEstimator, fmtEta: fmtEta, fmtTime: fmtTime, fmtSize: fmtSize,
    copy: copy, toast: toast, shareSheet: shareSheet, poller: poller
  };
})();
