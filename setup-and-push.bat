@echo off
setlocal
cd /d "%~dp0"
echo === claude-task-bar : local git init + push to GitHub ===
if exist ".git" (
  echo Removing partial .git created by the sandbox ...
  rmdir /s /q ".git"
)
git init
git add README.md .gitignore
git commit -m "chore: initial scaffold (README, .gitignore, design + mascot spec)"
git branch -M main
git remote add origin https://github.com/issei0423/claude-task-bar.git
git push -u origin main
echo.
echo Done. If a GitHub sign-in window appears, complete it once and the push continues.
echo Repo: https://github.com/issei0423/claude-task-bar
echo.
pause
endlocal
