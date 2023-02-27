chrome.contextMenus.create({
  id: "myContextMenu",
  title: "Paraphrase this",
  contexts: ["selection"]
});

chrome.contextMenus.onClicked.addListener(async function(info, tab) {
  if (info.menuItemId == "myContextMenu") {
    chrome.tabs.executeScript({
      code: "window.getSelection().toString();"
    }, async function(selection) {
      const apiUrl = "http://45.146.164.32:8080/Paraphrase?input=" + encodeURIComponent(selection[0]);

      try {
        const response = await fetch(apiUrl);
        const result = await response.text();
        alert(result.trim());
      } catch (error) {
        console.error(error);
      }
    });
  }
});

