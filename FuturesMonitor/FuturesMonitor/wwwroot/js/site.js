(() => {
  const root = document.querySelector("[data-dashboard]");

  if (!root) {
    return;
  }

  const endpoint = root.dataset.endpoint || "/api/dashboard";
  const refreshMs = Number(root.dataset.refreshMs || 3000);
  const numberFormatter = new Intl.NumberFormat("zh-TW", { maximumFractionDigits: 2 });
  const percentFormatter = new Intl.NumberFormat("zh-TW", {
    maximumFractionDigits: 2,
    minimumFractionDigits: 2
  });
  const dateFormatter = new Intl.DateTimeFormat("zh-TW", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
    timeZone: "Asia/Taipei"
  });

  const fields = {
    connection: root.querySelector('[data-field="connection"]'),
    connectionText: root.querySelector('[data-field="connectionText"]'),
    price: root.querySelector('[data-field="price"]'),
    priceChange: root.querySelector('[data-field="priceChange"]'),
    comparisonBase: root.querySelector('[data-field="comparisonBase"]'),
    quoteChart: root.querySelector('[data-field="quoteChart"]'),
    symbol: root.querySelector('[data-field="symbol"]'),
    sampleTime: root.querySelector('.quote-panel [data-field="sampleTime"]'),
    capturedAt: root.querySelector('[data-field="capturedAt"]'),
    age: root.querySelector('[data-field="age"]'),
    lastRefresh: root.querySelector('[data-field="lastRefresh"]'),
    recommendationSide: root.querySelector('[data-field="recommendationSide"]'),
    recommendationStatus: root.querySelector('[data-field="recommendationStatus"]'),
    triggerLight: root.querySelector('[data-field="triggerLight"]'),
    triggerRule: root.querySelector('[data-field="triggerRule"]'),
    triggerTime: root.querySelector('[data-field="triggerTime"]'),
    entryRange: root.querySelector('[data-field="entryRange"]'),
    stopLoss: root.querySelector('[data-field="stopLoss"]'),
    takeProfit: root.querySelector('[data-field="takeProfit"]'),
    rewardRisk: root.querySelector('[data-field="rewardRisk"]'),
    referenceEvent: root.querySelector('[data-field="referenceEvent"]'),
    atrRatio: root.querySelector('[data-field="atrRatio"]'),
    enteredPrice: root.querySelector('[data-field="enteredPrice"]'),
    confidence: root.querySelector('[data-field="confidence"]'),
    entryHitRate: root.querySelector('[data-field="entryHitRate"]'),
    outcome: root.querySelector('[data-field="outcome"]'),
    historyCount: root.querySelector('[data-field="historyCount"]'),
    message: root.querySelector('[data-field="message"]')
  };
  const recommendationPanel = root.querySelector("[data-recommendation]");
  const contextCards = new Map(
    Array.from(root.querySelectorAll("[data-context-side]")).map((card) => [card.dataset.contextSide, card])
  );
  const historyLists = new Map(
    Array.from(root.querySelectorAll("[data-history-side]")).map((lane) => [
      lane.dataset.historySide,
      lane.querySelector('[data-field="historyList"]')
    ])
  );

  let timerId = 0;
  let quoteHistory = [];

  if (fields.quoteChart && window.ResizeObserver) {
    const observer = new ResizeObserver(() => drawQuoteChart(quoteHistory));
    observer.observe(fields.quoteChart.parentElement);
  }

  async function refresh() {
    setConnection("warning", "刷新中");

    try {
      const response = await fetch(endpoint, { cache: "no-store" });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      render(await response.json());
    } catch (error) {
      setConnection("error", "讀取失敗");
      showMessage(`資料刷新失敗：${error.message}`);
    } finally {
      timerId = window.setTimeout(refresh, refreshMs);
    }
  }

  function render(snapshot) {
    setConnection(snapshot.isConnected ? "online" : "error", snapshot.isConnected ? "即時連線" : "連線異常");
    fields.lastRefresh.textContent = formatDate(snapshot.serverTimeTaipei);

    if (snapshot.quote) {
      renderQuote(snapshot.quote);
    } else {
      resetQuote();
    }

    quoteHistory = snapshot.quoteHistory || [];
    drawQuoteChart(quoteHistory);
    renderRecommendation(snapshot.latestRecommendation || null);
    renderMarketContexts(snapshot.marketContexts || []);
    renderRecommendationHistory(snapshot.recommendationHistory || []);

    if (snapshot.message) {
      showMessage(snapshot.message);
    } else {
      hideMessage();
    }
  }

  function renderQuote(quote) {
    fields.price.textContent = numberFormatter.format(quote.price);
    fields.symbol.textContent = quote.symbol;
    fields.sampleTime.textContent = formatDate(quote.sampleMinuteTaipei);
    fields.capturedAt.textContent = formatDate(quote.capturedAtTaipei);
    fields.age.textContent = formatAge(quote.ageSeconds);

    renderPriceChange(quote.priceChange, quote.priceChangePercent);
    renderComparisonBase(quote);
  }

  function resetQuote() {
    fields.price.textContent = "--";
    fields.priceChange.textContent = "--";
    fields.priceChange.className = "price-change";
    fields.priceChange.removeAttribute("title");
    fields.comparisonBase.textContent = "比較基準：--";
    fields.sampleTime.textContent = "--";
    fields.capturedAt.textContent = "--";
    fields.age.textContent = "--";
  }

  function drawQuoteChart(points) {
    const canvas = fields.quoteChart;

    if (!canvas) {
      return;
    }

    const rect = canvas.getBoundingClientRect();
    const width = Math.max(1, Math.floor(rect.width));
    const height = Math.max(1, Math.floor(rect.height));
    const dpr = window.devicePixelRatio || 1;

    if (canvas.width !== Math.floor(width * dpr) || canvas.height !== Math.floor(height * dpr)) {
      canvas.width = Math.floor(width * dpr);
      canvas.height = Math.floor(height * dpr);
    }

    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);

    drawChartGrid(ctx, width, height);

    if (!Array.isArray(points) || points.length < 2) {
      return;
    }

    const prices = points.map((point) => Number(point.price)).filter((price) => Number.isFinite(price));

    if (prices.length < 2) {
      return;
    }

    let min = Math.min(...prices);
    let max = Math.max(...prices);

    if (min === max) {
      min -= 1;
      max += 1;
    }

    const paddingX = 18;
    const paddingTop = 18;
    const paddingBottom = 26;
    const usableWidth = Math.max(1, width - paddingX * 2);
    const usableHeight = Math.max(1, height - paddingTop - paddingBottom);
    const rising = prices[prices.length - 1] >= prices[0];
    const lineColor = rising ? "rgba(50, 245, 166, .92)" : "rgba(255, 91, 127, .9)";
    const glowColor = rising ? "rgba(50, 245, 166, .28)" : "rgba(255, 91, 127, .24)";

    const coordinates = prices.map((price, index) => ({
      x: paddingX + (usableWidth * index) / (prices.length - 1),
      y: paddingTop + (max - price) / (max - min) * usableHeight
    }));

    const areaGradient = ctx.createLinearGradient(0, paddingTop, 0, height - paddingBottom);
    areaGradient.addColorStop(0, rising ? "rgba(50, 245, 166, .2)" : "rgba(255, 91, 127, .18)");
    areaGradient.addColorStop(1, "rgba(49, 215, 255, 0)");

    ctx.beginPath();
    ctx.moveTo(coordinates[0].x, height - paddingBottom);
    for (const point of coordinates) {
      ctx.lineTo(point.x, point.y);
    }
    ctx.lineTo(coordinates[coordinates.length - 1].x, height - paddingBottom);
    ctx.closePath();
    ctx.fillStyle = areaGradient;
    ctx.fill();

    drawLine(ctx, coordinates, glowColor, 8);
    drawLine(ctx, coordinates, lineColor, 2);

    const last = coordinates[coordinates.length - 1];
    ctx.beginPath();
    ctx.arc(last.x, last.y, 4.5, 0, Math.PI * 2);
    ctx.fillStyle = lineColor;
    ctx.shadowColor = lineColor;
    ctx.shadowBlur = 18;
    ctx.fill();
    ctx.shadowBlur = 0;
  }

  function drawChartGrid(ctx, width, height) {
    ctx.save();
    ctx.strokeStyle = "rgba(157, 176, 190, .08)";
    ctx.lineWidth = 1;

    for (let x = 24; x < width; x += 44) {
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, height);
      ctx.stroke();
    }

    for (let y = 24; y < height; y += 34) {
      ctx.beginPath();
      ctx.moveTo(0, y);
      ctx.lineTo(width, y);
      ctx.stroke();
    }

    ctx.restore();
  }

  function drawLine(ctx, coordinates, color, width) {
    ctx.save();
    ctx.beginPath();
    coordinates.forEach((point, index) => {
      if (index === 0) {
        ctx.moveTo(point.x, point.y);
      } else {
        const previous = coordinates[index - 1];
        const controlX = (previous.x + point.x) / 2;
        ctx.bezierCurveTo(controlX, previous.y, controlX, point.y, point.x, point.y);
      }
    });
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.lineJoin = "round";
    ctx.lineCap = "round";
    ctx.stroke();
    ctx.restore();
  }

  function renderPriceChange(change, changePercent) {
    fields.priceChange.className = "price-change";

    if (change === null || change === undefined) {
      fields.priceChange.textContent = "--";
      fields.priceChange.removeAttribute("title");
      return;
    }

    const numericChange = Number(change);
    const numericPercent = Number(changePercent || 0);
    const sign = numericChange > 0 ? "+" : "";

    fields.priceChange.textContent = `${sign}${numberFormatter.format(numericChange)} (${sign}${percentFormatter.format(numericPercent)}%)`;

    if (numericChange > 0) {
      fields.priceChange.classList.add("is-positive");
    } else if (numericChange < 0) {
      fields.priceChange.classList.add("is-negative");
    }
  }

  function renderComparisonBase(quote) {
    if (quote.comparisonBasePrice === null || quote.comparisonBasePrice === undefined || !quote.comparisonBaseTimeTaipei) {
      fields.comparisonBase.textContent = "比較基準：尚無前一個日盤收盤資料";
      fields.priceChange.removeAttribute("title");
      return;
    }

    const baseText = `前一個日盤收盤 ${numberFormatter.format(quote.comparisonBasePrice)}（${formatDate(quote.comparisonBaseTimeTaipei)}）`;
    fields.comparisonBase.textContent = `比較基準：${baseText}`;
    fields.priceChange.title = baseText;
  }

  function renderRecommendation(recommendation) {
    const classes = [
      "is-long",
      "is-short",
      "is-actionable",
      "is-entered",
      "is-success",
      "is-risk",
      "is-muted"
    ];

    recommendationPanel.classList.remove(...classes);
    fields.recommendationStatus.className = "recommendation-status";
    fields.triggerLight.className = "trigger-light";

    if (!recommendation) {
      fields.recommendationSide.textContent = "--";
      fields.recommendationStatus.textContent = "尚無事件";
      fields.triggerRule.textContent = "等待 15分K 突破／跌破 SMA76";
      fields.triggerTime.textContent = "--";
      fields.entryRange.textContent = "--";
      fields.stopLoss.textContent = "--";
      fields.takeProfit.textContent = "--";
      fields.rewardRisk.textContent = "--";
      fields.referenceEvent.textContent = "--";
      fields.atrRatio.textContent = "--";
      fields.enteredPrice.textContent = "--";
      fields.confidence.textContent = "--";
      fields.entryHitRate.textContent = "--";
      fields.outcome.textContent = "--";
      return;
    }

    const statusClass = getRecommendationStatusClass(recommendation.status);
    recommendationPanel.classList.add(`is-${recommendation.side}`, statusClass);
    fields.recommendationStatus.classList.add(statusClass);
    fields.triggerLight.classList.add("is-active");
    fields.recommendationSide.textContent = recommendation.sideLabel;
    fields.recommendationStatus.textContent = recommendation.statusLabel;
    fields.triggerRule.textContent = recommendation.triggerRuleLabel;
    fields.triggerTime.textContent = formatDate(recommendation.triggerAtTaipei);
    fields.entryRange.textContent = formatRange(recommendation.entryLow, recommendation.entryHigh);
    fields.stopLoss.textContent = formatPoint(recommendation.stopLoss);
    fields.takeProfit.textContent = formatPoint(recommendation.takeProfit);
    fields.rewardRisk.textContent = recommendation.rewardRiskRatio === null || recommendation.rewardRiskRatio === undefined
      ? "--"
      : `${numberFormatter.format(recommendation.rewardRiskRatio)} R`;
    fields.referenceEvent.textContent = recommendation.referenceEventId
      ? `#${recommendation.referenceEventId}`
      : "--";
    fields.atrRatio.textContent = recommendation.referenceAtrRatio === null || recommendation.referenceAtrRatio === undefined
      ? "--"
      : numberFormatter.format(recommendation.referenceAtrRatio);
    fields.enteredPrice.textContent = recommendation.enteredAtTaipei
      ? `${formatPoint(recommendation.entryPrice)} · ${formatDate(recommendation.enteredAtTaipei)}`
      : "--";
    fields.confidence.textContent = formatConfidence(recommendation);
    fields.entryHitRate.textContent = formatPercentRatio(recommendation.entryHitRate);
    fields.outcome.textContent = recommendation.outcomeLabel || "--";
  }

  function renderMarketContexts(contexts) {
    const bySide = new Map(contexts.map((context) => [context.side, context]));

    for (const [side, card] of contextCards) {
      const context = bySide.get(side);

      if (!context) {
        card.querySelector('[data-field="contextState"]').textContent = "--";
        card.querySelector('[data-field="status"]').textContent = "尚無盤勢資料";
        card.querySelector('[data-field="sampleTime"]').textContent = "--";
        card.querySelector('[data-field="consolidation"]').textContent = "盤整：--";
        renderConditions(card.querySelector('[data-field="conditions"]'), []);
        continue;
      }

      const passedCount = (context.conditions || []).filter((condition) => condition.passed).length;
      card.querySelector('[data-field="contextState"]').textContent = `${passedCount} 項亮起`;
      card.querySelector('[data-field="status"]').textContent = context.statusLabel;
      card.querySelector('[data-field="sampleTime"]').textContent = formatDate(context.sampleTimeTaipei);
      card.querySelector('[data-field="consolidation"]').textContent = `盤整：${context.consolidationLabel}`;
      renderConditions(card.querySelector('[data-field="conditions"]'), context.conditions || []);
    }
  }

  function renderRecommendationHistory(history) {
    const entries = Array.isArray(history) ? history : [];

    if (fields.historyCount) {
      fields.historyCount.textContent = `${entries.length} 筆`;
    }

    for (const [side, list] of historyLists) {
      if (!list) {
        continue;
      }

      const sideEntries = entries.filter((entry) => entry.side === side);
      if (sideEntries.length === 0) {
        renderEmptyHistory(list);
        continue;
      }

      const fragment = document.createDocumentFragment();
      for (const entry of sideEntries) {
        fragment.append(createHistoryRow(entry));
      }

      list.replaceChildren(fragment);
    }
  }

  function createHistoryRow(recommendation) {
    const row = document.createElement("div");
    const header = document.createElement("div");
    const time = document.createElement("span");
    const status = document.createElement("strong");
    const range = document.createElement("div");
    const metrics = document.createElement("div");
    const statusClass = getRecommendationStatusClass(recommendation.status);

    row.className = `history-row history-row-${recommendation.side || "unknown"} ${statusClass}`;
    header.className = "history-row-header";
    time.className = "history-time";
    status.className = `recommendation-status ${statusClass}`;
    range.className = "history-entry-range";
    metrics.className = "history-metrics";
    time.textContent = formatDate(recommendation.triggerAtTaipei);
    time.title = recommendation.triggerRuleLabel;
    status.textContent = recommendation.statusLabel;
    range.textContent = formatRange(recommendation.entryLow, recommendation.entryHigh);
    metrics.append(
      createHistoryMetric("停損", formatPoint(recommendation.stopLoss)),
      createHistoryMetric("停利", formatPoint(recommendation.takeProfit)),
      createHistoryMetric("R/R", formatPoint(recommendation.rewardRiskRatio)),
      createHistoryMetric("結果", recommendation.outcomeLabel || "--"),
      createHistoryMetric("信心", formatConfidence(recommendation))
    );

    header.append(time, status);
    row.append(header, range, metrics);
    return row;
  }

  function createHistoryMetric(label, value) {
    const metric = document.createElement("span");
    const labelElement = document.createElement("em");
    const valueElement = document.createElement("strong");

    metric.className = "history-metric";
    labelElement.textContent = label;
    valueElement.textContent = value;
    metric.append(labelElement, valueElement);

    return metric;
  }

  function renderEmptyHistory(list) {
    const empty = document.createElement("div");
    empty.className = "history-empty";
    empty.textContent = "尚無歷史事件";
    list.replaceChildren(empty);
  }

  function getRecommendationStatusClass(status) {
    if (status === "waiting_entry") {
      return "is-actionable";
    }

    if (status === "entered") {
      return "is-entered";
    }

    if (status === "take_profit") {
      return "is-success";
    }

    if (status === "stop_loss" || status === "invalid_price_structure") {
      return "is-risk";
    }

    if (status === "insufficient_price_data" || status === "no_reference_event" || status === "suppressed_active_recommendation") {
      return "is-muted";
    }

    return "is-waiting";
  }

  function renderConditions(container, conditions) {
    const fragment = document.createDocumentFragment();

    for (const condition of conditions) {
      const item = document.createElement("li");
      const light = document.createElement("span");
      const body = document.createElement("span");
      const label = document.createElement("span");
      const duration = document.createElement("small");

      item.className = `condition-item${condition.passed ? " is-passed" : ""}${condition.isAutomaticTrigger ? " is-trigger" : ""}`;
      light.className = "condition-light";
      body.className = "condition-body";
      label.className = "condition-label";
      duration.className = "condition-duration";
      label.textContent = condition.label;
      duration.textContent = formatConditionDuration(condition);

      body.append(label, duration);
      item.append(light, body);
      fragment.append(item);
    }

    container.replaceChildren(fragment);
  }

  function formatConditionDuration(condition) {
    const current = formatDuration(condition.passedDurationSeconds);
    const previous = formatDuration(condition.previousPassedDurationSeconds);

    if (condition.passed) {
      return previous
        ? `\u5df2\u7b26\u5408 ${current} / \u4e0a\u6b21 ${previous}`
        : `\u5df2\u7b26\u5408 ${current}`;
    }

    return previous
      ? `\u672a\u7b26\u5408 / \u4e0a\u6b21 ${previous}`
      : "\u672a\u7b26\u5408";
  }

  function formatDuration(seconds) {
    if (seconds === null || seconds === undefined) {
      return "--";
    }

    const value = Math.max(0, Number(seconds));
    if (value < 60) {
      return `${Math.round(value)} \u79d2`;
    }

    const totalMinutes = Math.max(1, Math.round(value / 60));
    if (totalMinutes < 60) {
      return `${totalMinutes} \u5206`;
    }

    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    return minutes === 0
      ? `${hours} \u5c0f\u6642`
      : `${hours} \u5c0f\u6642 ${minutes} \u5206`;
  }

  function formatConfidence(recommendation) {
    const sample = recommendation.confidenceSampleCount ?? 0;

    if (recommendation.confidenceScore === null || recommendation.confidenceScore === undefined) {
      return `${recommendation.confidenceLabel || "--"} (${sample})`;
    }

    return `${percentFormatter.format(Number(recommendation.confidenceScore) * 100)}% (${sample})`;
  }

  function formatPercentRatio(value) {
    return value === null || value === undefined
      ? "--"
      : `${percentFormatter.format(Number(value) * 100)}%`;
  }

  function setConnection(state, text) {
    fields.connection.classList.remove("is-online", "is-warning", "is-error");
    fields.connection.classList.add(`is-${state}`);
    fields.connectionText.textContent = text;
  }

  function showMessage(message) {
    fields.message.hidden = false;
    fields.message.textContent = message;
  }

  function hideMessage() {
    fields.message.hidden = true;
    fields.message.textContent = "";
  }

  function formatDate(value) {
    if (!value) {
      return "--";
    }

    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "--" : dateFormatter.format(date);
  }

  function formatAge(seconds) {
    if (seconds === null || seconds === undefined) {
      return "--";
    }

    const value = Math.max(0, Number(seconds));

    if (value < 60) {
      return `${Math.round(value)} 秒`;
    }

    return `${Math.floor(value / 60)} 分 ${Math.round(value % 60)} 秒`;
  }

  function formatPoint(value) {
    return value === null || value === undefined ? "--" : numberFormatter.format(value);
  }

  function formatRange(low, high) {
    if (low === null || low === undefined || high === null || high === undefined) {
      return "--";
    }

    return `${numberFormatter.format(low)} - ${numberFormatter.format(high)}`;
  }

  window.addEventListener("beforeunload", () => {
    window.clearTimeout(timerId);
  });

  refresh();
})();
