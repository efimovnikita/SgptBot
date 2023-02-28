const button1 = document.createElement("button");
button1.innerHTML = "S";
button1.classList.add("my-button-class");
button1.style.bottom = "204px";

document.body.appendChild(button1);

const textArea = document.querySelector(
  "#__next > div.overflow-hidden.w-full.h-full.relative > div.flex.h-full.flex-1.flex-col.md\\:pl-\\[260px\\] > main > div.absolute.bottom-0.left-0.w-full.border-t.md\\:border-t-0.dark\\:border-white\\/20.md\\:border-transparent.md\\:dark\\:border-transparent.md\\:bg-vert-light-gradient.bg-white.dark\\:bg-gray-800.md\\:\\!bg-transparent.dark\\:md\\:bg-vert-dark-gradient > form > div > div.flex.flex-col.w-full.py-2.flex-grow.md\\:py-3.md\\:pl-4.relative.border.border-black\\/10.bg-white.dark\\:border-gray-900\\/50.dark\\:text-white.dark\\:bg-gray-700.rounded-md.shadow-\\[0_0_10px_rgba\\(0\\,0\\,0\\,0\\.10\\)\\].dark\\:shadow-\\[0_0_15px_rgba\\(0\\,0\\,0\\,0\\.10\\)\\] > textarea"
);

button1.addEventListener("click", () => {
  textArea.value += "Rewrite this in more simple words:\n\n";
  setHeightAndFocus();
});

const button2 = document.createElement("button");
button2.innerHTML = "A1";
button2.classList.add("my-button-class");
button2.style.bottom = "144px";

document.body.appendChild(button2);

button2.addEventListener("click", () => {
  textArea.value += "Rewrite this and use A1 English vocabulary:\n\n";
  setHeightAndFocus();
});

const button3 = document.createElement("button");
button3.innerHTML = "A2";
button3.classList.add("my-button-class");
button3.style.bottom = "84px";

document.body.appendChild(button3);

button3.addEventListener("click", () => {
  textArea.value += "Rewrite this and use A2 English vocabulary:\n\n";
  setHeightAndFocus();
});

const button4 = document.createElement("button");
button4.innerHTML = "B1";
button4.classList.add("my-button-class");
button4.style.bottom = "24px";

document.body.appendChild(button4);

button4.addEventListener("click", () => {
  textArea.value += "Rewrite this and use B1 English vocabulary:\n\n";
  setHeightAndFocus();
});

function setHeightAndFocus() {
  textArea.style.height = "200px";
  textArea.focus();
}
// setTimeout(() => {
//   const container = document.querySelector(
//     "#__next > div.overflow-hidden.w-full.h-full.relative > div.flex.h-full.flex-1.flex-col.md\\:pl-\\[260px\\] > main > div.absolute.bottom-0.left-0.w-full.border-t.md\\:border-t-0.dark\\:border-white\\/20.md\\:border-transparent.md\\:dark\\:border-transparent.md\\:bg-vert-light-gradient.bg-white.dark\\:bg-gray-800.md\\:\\!bg-transparent.dark\\:md\\:bg-vert-dark-gradient > form > div"
//   );

//   const button1 = document.createElement("p");
//   button1.textContent = "Simple rewrite";
//   button1.classList.add("my-button-class");

//   const button2 = document.createElement("p");
//   button2.textContent = "A1";
//   button2.classList.add("my-button-class");

//   const button3 = document.createElement("p");
//   button3.textContent = "A2";
//   button3.classList.add("my-button-class");

//   const button4 = document.createElement("p");
//   button4.textContent = "B1";
//   button4.classList.add("my-button-class");

//   const textArea = document.querySelector(
//     "#__next > div.overflow-hidden.w-full.h-full.relative > div.flex.h-full.flex-1.flex-col.md\\:pl-\\[260px\\] > main > div.absolute.bottom-0.left-0.w-full.border-t.md\\:border-t-0.dark\\:border-white\\/20.md\\:border-transparent.md\\:dark\\:border-transparent.md\\:bg-vert-light-gradient.bg-white.dark\\:bg-gray-800.md\\:\\!bg-transparent.dark\\:md\\:bg-vert-dark-gradient > form > div > div.flex.flex-col.w-full.py-2.flex-grow.md\\:py-3.md\\:pl-4.relative.border.border-black\\/10.bg-white.dark\\:border-gray-900\\/50.dark\\:text-white.dark\\:bg-gray-700.rounded-md.shadow-\\[0_0_10px_rgba\\(0\\,0\\,0\\,0\\.10\\)\\].dark\\:shadow-\\[0_0_15px_rgba\\(0\\,0\\,0\\,0\\.10\\)\\] > textarea"
//   );

//   button1.addEventListener("click", () => {
//     textArea.value += "Rewrite this in more simple words:\n\n";
//     textArea.style.height = "200px";
//   });

//   button2.addEventListener("click", () => {
//     textArea.value += "Rewrite this and use A1 English vocabulary:\n\n";
//     textArea.style.height = "200px";
//   });

//   button3.addEventListener("click", () => {
//     textArea.value += "Rewrite this and use A2 English vocabulary:\n\n";
//     textArea.style.height = "200px";
//   });

//   button4.addEventListener("click", () => {
//     textArea.value += "Rewrite this and use B1 English vocabulary:\n\n";
//     textArea.style.height = "200px";
//   });

//   const buttonContainer = document.createElement("div");
//   buttonContainer.appendChild(button1);
//   buttonContainer.appendChild(button2);
//   buttonContainer.appendChild(button3);
//   buttonContainer.appendChild(button4);
//   buttonContainer.classList.add("my-container");

//   container.appendChild(buttonContainer);
// }, 1000);
