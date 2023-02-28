chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) {
  chrome.tabs.executeScript(
    tabs[0].id,
    {
      code: `
        var s1 = document.querySelector("#lesson-reader > main > div > article > div > div");
        var text = '';
        for (var i = 0; i < s1.children.length; i++) {
          if (s1.children[i].tagName === "P") {
            text += s1.children[i].textContent.trim() + '\\n\\n';
          } else {
            text += s1.children[i].textContent.trim() + ' ';
          }
        }
        text.trim();
        text;
      `,
    },
    function (result) {
      document.getElementById("output").innerText = result[0];
    }
  );
});
